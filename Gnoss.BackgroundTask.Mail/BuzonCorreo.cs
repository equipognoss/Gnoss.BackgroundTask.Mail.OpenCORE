using Es.Riam.AbstractsOpen;
using Es.Riam.Gnoss.AD.EntityModel;
using Es.Riam.Gnoss.AD.EntityModelBASE;
using Es.Riam.Gnoss.AD.Virtuoso;
using Es.Riam.Gnoss.CL;
using Es.Riam.Gnoss.Logica.BASE_BD;
using Es.Riam.Gnoss.Servicios;
using Es.Riam.Gnoss.Util.Configuracion;
using Es.Riam.Gnoss.Util.General;
using Es.Riam.Interfaces;
using Es.Riam.Util.Correo;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Es.Riam.Util
{
    class BuzonCorreo : ControladorServicioGnoss
    {
        public static int LIMITE_CORREOS = 25;
        public static string FICHERO_GENERAL = "logGeneral";
        public static string FICHERO_DESTINATARIOS_FALLIDOS = "destinatarios_fallidos";

        #region Miembros
        private string mBuzon;
        private Dictionary<DateTime, int> mHistorialEnvios;
        private List<Email> mCorreosEnviar;
        private Task tarea;
        private BaseComunidadCN mBaseComunidadCN;
        private CancellationTokenSource mCancellationToken;
        private EntityContext mEntityContext;
        //private string mFicheroConfiguracionBDBase;
        //private string mFicheroConfiguracionBDOriginal;
        string mDirectorioLog;
        #endregion

        #region Constructores
        public BuzonCorreo(string pBuzon, IServiceScopeFactory pServiceScopeFactory, ConfigService pConfigService, string pDirectorioLog) : base(pServiceScopeFactory, pConfigService)
        {
            mBuzon = pBuzon;
            mHistorialEnvios = new Dictionary<DateTime, int>();
            //mFicheroConfiguracionBDBase = pFicheroConfiguracionBD;
            //mFicheroConfiguracionBDOriginal = pFicheroConfiguracionBDOriginal;

            mDirectorioLog = pDirectorioLog + Path.DirectorySeparatorChar + mBuzon.Replace("http://", "").Replace("https://", "").Replace("/", "_");
            DirectoryInfo directorioLog = new DirectoryInfo(mDirectorioLog);
            if (!directorioLog.Exists)
            {
                directorioLog.Create();
            }
        }
        #endregion

        #region Metodos
        #region Publicos
        /// <summary>
        /// Determina si el hilo esta vivo aun
        /// </summary>
        /// <returns></returns>
        public bool EstaVivo()
        {
            if (tarea.IsCanceled || tarea.IsCompleted || tarea.IsFaulted)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Determina si el buzon a cumplido el cupo de mensajes establecidos para 1 minuto
        /// </summary>
        /// <returns></returns>
        public bool PuedoEnviar(bool esRabbit = false)
        {
            if (esRabbit || !EstaVivo())
            {
                if (mHistorialEnvios.Count > 0)
                {
                    int enviosUltimoMinuto = 0;
                    DateTime fechaLimite = DateTime.Now;
                    fechaLimite = fechaLimite.AddMinutes(-1);
                    Dictionary<DateTime, int> historialUltimoMinuto = mHistorialEnvios.Where(p => p.Key >= fechaLimite).ToDictionary(p => p.Key, p => p.Value);

                    foreach (DateTime fecha in historialUltimoMinuto.Keys)
                    {
                        enviosUltimoMinuto += historialUltimoMinuto[fecha];
                    }

                    if (enviosUltimoMinuto >= LIMITE_CORREOS)
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Inicializa el hilo
        /// </summary>
        public void LanzarEnvio()
        {
            tarea = Task.Factory.StartNew(() => Procesar(), ObtenerTokenCancelacionDeControlador().Token);
        }


        #endregion

        #region Privados
        private CancellationTokenSource ObtenerTokenCancelacionDeControlador()
        {
            mCancellationToken = new CancellationTokenSource();
            return mCancellationToken;
        }

        /// <summary>
        /// Método que cancela un hilo de manera segura.
        /// </summary>
        private void ComprobarCancelacionHilo()
        {
            if (mCancellationToken.Token != null)
            {
                mCancellationToken.Token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Prepara los correo a enviar de entre todos los pendientes
        /// </summary>
        /// <returns></returns>
        private List<Email> PrepararCorreos()
        {
            List<Email> posiblesCorreos = mBaseComunidadCN.ObtenerColaCorreoBuzon(mBuzon);
            List<Email> correos = new List<Email>();
            int totalCorreos = 0;

            foreach (Email email in posiblesCorreos)
            {
                if ((totalCorreos + email.Destinatarios.Count) <= LIMITE_CORREOS)
                {
                    totalCorreos += email.Destinatarios.Count;
                    correos.Add(email);
                }
                else
                {
                    email.Destinatarios = email.Destinatarios.Take(LIMITE_CORREOS - totalCorreos).ToList();
                    totalCorreos += email.Destinatarios.Count;
                    correos.Add(email);
                }
            }
            return correos;
        }

        /// <summary>
        /// Prepara los correo a enviar de entre todos los pendientes
        /// </summary>
        /// <returns></returns>
        private List<Email> PrepararCorreosRabbit(int pCorreoID)
        {
            List<Email> posiblesCorreos = mBaseComunidadCN.ObtenerColaCorreoEmailCorreoID(pCorreoID);
            List<Email> correos = new List<Email>();
            int totalCorreos = 0;

            foreach (Email email in posiblesCorreos)
            {
                if ((totalCorreos + email.Destinatarios.Count) <= LIMITE_CORREOS)
                {
                    totalCorreos += email.Destinatarios.Count;
                    correos.Add(email);
                }
                else
                {
                    email.Destinatarios = email.Destinatarios.Take(LIMITE_CORREOS - totalCorreos).ToList();
                    totalCorreos += email.Destinatarios.Count;
                    correos.Add(email);
                }
            }
            return correos;
        }

        public bool HayCorreosPendientes(int pCorreoID, EntityContext pEntityContext, EntityContextBASE pEntityContextBASE, LoggingService pLogginService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            mBaseComunidadCN = new BaseComunidadCN(pEntityContext, pLogginService, pEntityContextBASE, mConfigService, servicesUtilVirtuosoAndReplication);
            return mBaseComunidadCN.HayCorreosPendientesBuzon(pCorreoID);
        }

        /// <summary>
        /// Procesa los correos pendientes
        /// </summary>
        public void Procesar(bool pEsRabbit = false, int pCorreoID = 0)
        {
            using (var scope = ScopedFactory.CreateScope())
            {
                EntityContext entityContext = scope.ServiceProvider.GetRequiredService<EntityContext>();
                EntityContextBASE entityContextBASE = scope.ServiceProvider.GetRequiredService<EntityContextBASE>();
                UtilidadesVirtuoso utilidadesVirtuoso = scope.ServiceProvider.GetRequiredService<UtilidadesVirtuoso>();
                LoggingService loggingService = scope.ServiceProvider.GetRequiredService<LoggingService>();
                IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication = scope.ServiceProvider.GetRequiredService<IServicesUtilVirtuosoAndReplication>();

                EmpezarMantenimiento();
                mBaseComunidadCN = new BaseComunidadCN(entityContext, loggingService, entityContextBASE, mConfigService, servicesUtilVirtuosoAndReplication);

                if (pEsRabbit)
                {
                    mCorreosEnviar = PrepararCorreosRabbit(pCorreoID);
                }
                else
                {
                    mCorreosEnviar = PrepararCorreos();
                }

                LogStatus estadoProceso = LogStatus.Error;
                int numeroEmails = mCorreosEnviar.Sum(correo => correo.Destinatarios.Count);
                mHistorialEnvios.Add(DateTime.Now, numeroEmails);
                try
                {
                    estadoProceso = EnviarCorreo(pEsRabbit);
                    string entradaLog = string.Empty;
                    switch (estadoProceso)
                    {
                        case LogStatus.Correcto:
                            entradaLog = LogStatus.Enviando.ToString().ToUpper() + " (" + mFicheroConfiguracionBDBase + ") " + UtilLogs.CrearEntradaRegistro(estadoProceso, "Todos los mensajes enviados");
                            break;
                        case LogStatus.NoEnviado:
                            entradaLog = LogStatus.Enviando.ToString().ToUpper() + " (" + mFicheroConfiguracionBDBase + ") " + UtilLogs.CrearEntradaRegistro(estadoProceso, "No hay mensajes pendientes de enviar...");
                            break;
                        case LogStatus.Error:
                            entradaLog = LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBDBase + ") " + UtilLogs.CrearEntradaRegistro(estadoProceso, "Hay mensajes que no se han podido enviar");
                            break;
                    }

                    //Escribe entrada en Log
                    UtilLogs.GuardarLog(entradaLog, "", mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_GENERAL);
                }
                catch (OperationCanceledException)
                {
                }
                catch (System.Net.Mail.SmtpException smtpEx)
                {
                    //La configuración del buzon de correo (cuenta, usuario, password) es erronea, se escribe en el log y se detiene el servicio.
                    UtilLogs.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBDBase + ") " + UtilLogs.CrearEntradaRegistro(LogStatus.Error, "La configuración del buzón (dirección de correo, usuario y contraseña) no es válida: " + smtpEx.Message + " : " + smtpEx.StackTrace), "", mDirectorioLog + Path.DirectorySeparatorChar + "logGeneral");

                    //Recogemos el innerexception antes de lanzar el nuevo exception:
                    if (smtpEx.InnerException != null)
                    {
                        UtilLogs.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBDBase + ") InnerExceptionMessage: " + UtilLogs.CrearEntradaRegistro(LogStatus.Error, smtpEx.InnerException.Message) + " InnerExceptionStackTrace: " + smtpEx.InnerException.StackTrace, "", mDirectorioLog + Path.DirectorySeparatorChar + "logGeneral");
                    }
                }
                catch (Exception ex)
                {
                    UtilLogs.GuardarExcepcion(ex, mFicheroConfiguracionBDBase, mDirectorioLog + Path.DirectorySeparatorChar + "logGeneral");
                }
            }
        }

        /// <summary>
        /// Realiza el envío de las notificaciones
        /// </summary>
        /// <returns>Estado del resultado de la operacion del envio de las notificaciones</returns>
        private LogStatus EnviarCorreo(bool esRabbit = false)
        {
            LogStatus estadoProceso = LogStatus.Correcto;
            foreach (Email correo in mCorreosEnviar)
            {
                if (!esRabbit)
                {
                    ComprobarCancelacionHilo();
                }
                estadoProceso = LogStatus.Correcto;
                ICorreo gestorCorreo = CargarGestorCorreo(correo, estadoProceso);

                if (estadoProceso != LogStatus.Error)
                {
                    EnviarCorreoDestinatarios(gestorCorreo, correo, estadoProceso);
                }
            }
            return estadoProceso;

        }

        /// <summary>
        /// Carga el gestor de correo para cada linea de la tabla colaCorreo
        /// </summary>
        /// <param name="pCorreo">Datos del correo</param>
        /// <param name="pEstadoProceso">Estado del envio</param>
        /// <returns></returns>
        private ICorreo CargarGestorCorreo(Email pCorreo, LogStatus pEstadoProceso)
        {
            ICorreo gestorCorreo = null;
            try
            {
                gestorCorreo = ObtenerGestorCorreo(pCorreo);
            }
            catch (Exception ex)
            {
                if (gestorCorreo != null)
                {
                    gestorCorreo.Dispose();
                }
                gestorCorreo = null;

                foreach (DestinatarioEmail destinatario in pCorreo.Destinatarios)
                {
                    destinatario.Estado = 2;
                    destinatario.FechaProcesado = DateTime.Now;
                    mBaseComunidadCN.ModificarEstadoCorreo(pCorreo.CorreoID, destinatario.Email, destinatario.Estado);
                    string mensajeError = "No se ha podido enviar el correo: " + destinatario.CorreoID + " al destinatario: " + destinatario.Email;
                    UtilLogs.GuardarLog(mensajeError, "", mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_DESTINATARIOS_FALLIDOS);
                }

                UtilLogs.GuardarLog("Error al obtener el gestor de correo, para el correoID " + pCorreo.CorreoID + " --> " + ex.Message + "  " + ex.StackTrace, "", mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_GENERAL);
                pEstadoProceso = LogStatus.Error;
            }
            return gestorCorreo;
        }

        /// <summary>
        /// Carga un gestor de correo en funcion del tipo de correo configurado
        /// </summary>
        /// <param name="correo">Datos del correo</param>
        /// <returns></returns>
        private ICorreo ObtenerGestorCorreo(Email correo)
        {
            if (correo.Tipo.ToLower().Equals("smtp"))
            {
                return new UtilCorreo(correo.ServidorCorreo.SMTP, correo.ServidorCorreo.Puerto, correo.ServidorCorreo.Usuario, correo.ServidorCorreo.Password, correo.ServidorCorreo.EsSeguro);
            }
            else
            {           
                return new UtilEws(correo.ServidorCorreo.Usuario, correo.ServidorCorreo.Password, correo.ServidorCorreo.SMTP);
            }
        }

        /// <summary>
        /// Envia el correo a cada uno de los destinatarios
        /// </summary>
        /// <param name="pGestorCorreo">El gestor de correo a utilizar</param>
        /// <param name="pCorreo">Datos del correo a enviar</param>
        /// <param name="pEstadoProceso">Estado del proceso</param>
        private void EnviarCorreoDestinatarios(ICorreo pGestorCorreo, Email pCorreo, LogStatus pEstadoProceso)
        {
            try
            {
                foreach (DestinatarioEmail destinatario in pCorreo.Destinatarios)
                {
                    try
                    {
                        pGestorCorreo.EnviarCorreo(pCorreo, destinatario);
                    }
                    //si no se ha enviado guardamos el log
                    catch (Exception ex)
                    {
                        string mensajeError = "No se ha podido enviar el correo: " + destinatario.CorreoID + " al destinatario: " + destinatario.Email;
                        UtilLogs.GuardarLog(mensajeError, "", mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_DESTINATARIOS_FALLIDOS);
                        UtilLogs.GuardarExcepcion(ex, mFicheroConfiguracionBDBase, mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_GENERAL);
                    }
                    finally
                    {
                        mBaseComunidadCN.ModificarEstadoCorreo(pCorreo.CorreoID, destinatario.Email, destinatario.Estado);
                    }
                }

                UtilLogs.GuardarCorreo(pCorreo, mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_GENERAL);
                mBaseComunidadCN.BorrarCorreosEnviadosCorrectamente(pCorreo.CorreoID);

                bool correosNoEnviados = mBaseComunidadCN.ComprobarCorreosPendientesEnviar(pCorreo.CorreoID);
                if (!correosNoEnviados)
                {
                    mBaseComunidadCN.BorrarCorreo(pCorreo.CorreoID);
                }
            }
            catch (Exception ex)
            {
                pEstadoProceso = LogStatus.Error;
                UtilLogs.GuardarExcepcion(ex, mFicheroConfiguracionBDBase, mDirectorioLog + Path.DirectorySeparatorChar + FICHERO_GENERAL);
            }
            finally
            {
                pGestorCorreo.Dispose();
            }
        }

        protected override ControladorServicioGnoss ClonarControlador()
        {
            return null;
        }

        public override void RealizarMantenimiento(EntityContext entityContext, EntityContextBASE entityContextBASE, UtilidadesVirtuoso utilidadesVirtuoso, LoggingService loggingService, RedisCacheWrapper redisCacheWrapper, GnossCache gnossCache, VirtuosoAD virtuosoAD, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
        }

        #endregion
        #endregion
    }
}
