using Es.Riam.Gnoss.Logica.BASE_BD;
using Es.Riam.Gnoss.Servicios;
using Es.Riam.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Es.Riam.Gnoss.RabbitMQ;
using Es.Riam.Gnoss.Util.General;
using Es.Riam.Gnoss.Recursos;
using Es.Riam.Gnoss.Logica.Organizador.Correo;
using Es.Riam.Gnoss.AD.EntityModelBASE.Models;
using Microsoft.Extensions.DependencyInjection;
using Es.Riam.Gnoss.Util.Configuracion;
using Es.Riam.Gnoss.AD.EntityModel;
using Es.Riam.Gnoss.AD.EntityModelBASE;
using Es.Riam.Gnoss.CL;
using Es.Riam.Gnoss.AD.Virtuoso;
using Es.Riam.AbstractsOpen;

namespace Es.Riam.Gnoss.Win.ServicioCorreo.Principal
{
    class CorreoController : ControladorServicioGnoss
    {
        #region Constantes

        private const string COLA_CORREO = "ColaCorreo";
        private const string EXCHANGE = "";

        #endregion

        #region Miembros
        List<string> mListaBuzones;
        Dictionary<string, BuzonCorreo> mDicBuzones;
        BaseComunidadCN mBaseComunidadCN;
        string mDirectorioLog;
        #endregion

        #region Constructor

        /// <summary>
        /// Constructor a partir de la base de datos pasada por parámetro
        /// </summary>
        /// <param name="pBaseDeDatos">Base de datos</param>
        public CorreoController(IServiceScopeFactory serviceScope, ConfigService configService, int sleep = 0)
            : base(serviceScope, configService)
        {
            mListaBuzones = new List<string>();
            mDicBuzones = new Dictionary<string, BuzonCorreo>();
            mDirectorioLog = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + Path.DirectorySeparatorChar + "logs_correos";
            DirectoryInfo directorioLog = new DirectoryInfo(mDirectorioLog);
            if (!directorioLog.Exists)
            {
                directorioLog.Create();
            }
        }

        protected override ControladorServicioGnoss ClonarControlador()
        {
            return new CorreoController(ScopedFactory, mConfigService);
        }

        #endregion

        #region Metodos
        #region Publicos

        /// <summary>
        /// Realiza el envio de los correos pendientes de enviar y escribe en el fichero de log una entrada 
        /// indicando el resultado de la operación
        /// </summary>
        public override void RealizarMantenimiento(EntityContext entityContext, EntityContextBASE entityContextBASE, UtilidadesVirtuoso utilidadesVirtuoso, LoggingService loggingService, RedisCacheWrapper redisCacheWrapper, GnossCache gnossCache, VirtuosoAD virtuosoAD, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            RealizarMantenimientoRabbitMQ(loggingService);
            RealizarMantenimientoBD(loggingService, entityContext, entityContextBASE, servicesUtilVirtuosoAndReplication);
        }
        #endregion

        #region Privados      

        private void RealizarMantenimientoBD(LoggingService pLoggingService, EntityContext pEntityContext, EntityContextBASE pEntityContextBASE, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            while (true)
            {
                try
                {
                    ComprobarCancelacionHilo();

                    if (mReiniciarLecturaRabbit)
                    {
                        RealizarMantenimientoRabbitMQ(pLoggingService);
                    }

                    if (mBaseComunidadCN == null)
                    {
                        mBaseComunidadCN = new BaseComunidadCN(pEntityContext,  pLoggingService, pEntityContextBASE, mConfigService, servicesUtilVirtuosoAndReplication);
                    }

                    CargarDatos();
                    foreach (string buzon in mListaBuzones)
                    {
                        BuzonCorreo buzonCorreo;
                        if (mDicBuzones.ContainsKey(buzon))
                        {
                            buzonCorreo = mDicBuzones[buzon];
                            if (buzonCorreo.PuedoEnviar())
                            {
                                buzonCorreo.LanzarEnvio();
                            }
                        }
                        else
                        {
                            buzonCorreo = new BuzonCorreo(buzon, ScopedFactory, mConfigService, mDirectorioLog);
                            mDicBuzones.Add(buzon, buzonCorreo);
                            buzonCorreo.LanzarEnvio();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    UtilLogs.GuardarLog($"Error al iniciar buzones --> {ex.Message} {ex.StackTrace}", "", $"{mDirectorioLog}{Path.DirectorySeparatorChar}logGeneral");
                }
                finally
                {
                    int tiempoEsperaAleatorio = INTERVALO_SEGUNDOS;
                    if (mBaseComunidadCN == null)
                    {
                        Random aleatorio = new Random();
                        tiempoEsperaAleatorio += aleatorio.Next(1, 10);
                    }
                    Thread.Sleep(tiempoEsperaAleatorio * 1000);
                }
            }
        }

        private void RealizarMantenimientoRabbitMQ(LoggingService logginService, bool reintentar = true)
        {
            if (mConfigService.ExistRabbitConnection(RabbitMQClient.BD_SERVICIOS_WIN))
            {
                RabbitMQClient.ReceivedDelegate funcionProcesarItem = new RabbitMQClient.ReceivedDelegate(ProcesarItem);
                RabbitMQClient.ShutDownDelegate funcionShutDown = new RabbitMQClient.ShutDownDelegate(OnShutDown);

                RabbitMqClientLectura = new RabbitMQClient(RabbitMQClient.BD_SERVICIOS_WIN, COLA_CORREO, logginService, mConfigService, EXCHANGE, COLA_CORREO);

                try
                {
                    RabbitMqClientLectura.ObtenerElementosDeCola(funcionProcesarItem, funcionShutDown);
                    mReiniciarLecturaRabbit = false;
                }
                catch (Exception ex)
                {
                    mReiniciarLecturaRabbit = true;
                    logginService.GuardarLogError(ex);
                }
            }
        }

        private bool ProcesarItem(string pFila)
        {
            using (var scope = ScopedFactory.CreateScope())
            {

                EntityContext entityContext = scope.ServiceProvider.GetRequiredService<EntityContext>();
                EntityContextBASE entityContextBASE = scope.ServiceProvider.GetRequiredService<EntityContextBASE>();
                UtilidadesVirtuoso utilidadesVirtuoso = scope.ServiceProvider.GetRequiredService<UtilidadesVirtuoso>();
                LoggingService loggingService = scope.ServiceProvider.GetRequiredService<LoggingService>();
                VirtuosoAD virtuosoAD = scope.ServiceProvider.GetRequiredService<VirtuosoAD>();
                RedisCacheWrapper redisCacheWrapper = scope.ServiceProvider.GetRequiredService<RedisCacheWrapper>();
                GnossCache gnossCache = scope.ServiceProvider.GetRequiredService<GnossCache>();
                IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication = scope.ServiceProvider.GetRequiredService<IServicesUtilVirtuosoAndReplication>();

                ComprobarCancelacionHilo();
                try
                {
                    System.Diagnostics.Debug.WriteLine($"ProcesarItem, {pFila}!");

                    if (!string.IsNullOrEmpty(pFila))
                    {
                        int correoID = JsonConvert.DeserializeObject<int>(pFila);

                        ProcesarCorreo(correoID, entityContext, entityContextBASE, loggingService, servicesUtilVirtuosoAndReplication);

                        servicesUtilVirtuosoAndReplication.ConexionAfinidad = "";

                        ControladorConexiones.CerrarConexiones(false);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    loggingService.GuardarLogError(ex);
                    return true;
                }
            }
        }

        private void ProcesarCorreo(int pCorreoID, EntityContext pEntityContext, EntityContextBASE pEntityContextBASE, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            BaseComunidadCN baseComunidadCN = new BaseComunidadCN(pEntityContext, pLoggingService, pEntityContextBASE, mConfigService, servicesUtilVirtuosoAndReplication);
            ColaCorreo colaCorreo = baseComunidadCN.ObtenerColaCorreoCorreoID(pCorreoID);
            string smtp = colaCorreo.SMTP;

            BuzonCorreo buzonCorreo;
            if (mDicBuzones.ContainsKey(smtp))
            {
                buzonCorreo = mDicBuzones[smtp];

                while (buzonCorreo.HayCorreosPendientes(pCorreoID, pEntityContext, pEntityContextBASE, pLoggingService, servicesUtilVirtuosoAndReplication))
                {
                    if (buzonCorreo.PuedoEnviar(true))
                    {
                        buzonCorreo.Procesar(true, pCorreoID);
                    }
                    else
                    {
                        Thread.Sleep(60000);
                    }
                }

            }
            else
            {
                buzonCorreo = new BuzonCorreo(smtp, ScopedFactory, mConfigService, mDirectorioLog);
                mDicBuzones.Add(smtp, buzonCorreo);
                while (buzonCorreo.HayCorreosPendientes(pCorreoID, pEntityContext, pEntityContextBASE, pLoggingService, servicesUtilVirtuosoAndReplication))
                {
                    if (buzonCorreo.PuedoEnviar(true))
                    {
                        buzonCorreo.Procesar(true, pCorreoID);
                    }
                    else
                    {
                        Thread.Sleep(60000);
                    }
                }
            }
        }

        private void CargarDatos()
        {
            mListaBuzones = mBaseComunidadCN.ObtenerBuzonesCorreo();
        }
        #endregion
        #endregion
    }
}
