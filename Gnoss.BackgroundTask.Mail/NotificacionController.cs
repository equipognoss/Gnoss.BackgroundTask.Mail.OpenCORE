using System;
using System.Collections.Generic;
using System.Diagnostics;
using Es.Riam.Gnoss.Elementos.Notificacion;
using Es.Riam.Gnoss.Logica.Notificacion;
using Es.Riam.Gnoss.AD.Notificacion;
using Es.Riam.Util;
using System.Threading;
using Es.Riam.Gnoss.Logica.ServiciosGenerales;
using Es.Riam.Gnoss.Recursos;
using Es.Riam.Gnoss.AD.ServiciosGenerales;
using Es.Riam.Gnoss.Logica.ParametroAplicacion;
using System.Text.RegularExpressions;
using Es.Riam.Gnoss.Servicios;
using Es.Riam.Gnoss.Logica.ParametrosProyecto;
using System.Net.Mail;
using System.Data;
using Es.Riam.Gnoss.Logica.Parametro;
using Es.Riam.Gnoss.AD.Parametro;
using Es.Riam.Interfaces;
using Es.Riam.Gnoss.AD.ParametroAplicacion;
using Es.Riam.Gnoss.AD.EntityModel.Models.ParametroGeneralDS;
using Es.Riam.Gnoss.Elementos.ParametroGeneralDSEspacio;
using System.Linq;
using Es.Riam.Gnoss.AD.EntityModel;
using Es.Riam.Gnoss.Web.Controles.ParametroGeneralDSName;
using Es.Riam.Gnoss.AD.EntityModel.Models.ProyectoDS;
using Es.Riam.Gnoss.RabbitMQ;
using Es.Riam.Gnoss.Util.General;
using Newtonsoft.Json;
using Es.Riam.Gnoss.AD.EntityModel.Models;
using Microsoft.Extensions.DependencyInjection;
using Es.Riam.Gnoss.Util.Configuracion;
using Es.Riam.Gnoss.AD.EntityModelBASE;
using Es.Riam.Gnoss.CL;
using Es.Riam.Gnoss.AD.Virtuoso;
using Es.Riam.AbstractsOpen;

namespace Es.Riam.Gnoss.Win.ServicioCorreo
{
    #region Enumeraciones

    /// <summary>
    /// Estado del envío
    /// </summary>
    public enum LogStatus
    {
        /// <summary>
        /// Inicio
        /// </summary>
        Inicio,
        /// <summary>
        /// Parada
        /// </summary>
        Parada,
        /// <summary>
        /// Correcto
        /// </summary>
        Correcto,
        /// <summary>
        /// Enviando
        /// </summary>
        Enviando,
        /// <summary>
        /// Error
        /// </summary>
        Error,
        /// <summary>
        /// NoEnviado
        /// </summary>
        NoEnviado
    }

    #endregion

    public class NotificacionController : ControladorServicioGnoss
    {
        #region Constantes

        private const string COLA_NOTIFICACION = "ColaNotificacion";
        private const string EXCHANGE = "";

        #endregion

        #region Miembros

        ///// <summary>
        ///// Base de datos
        ///// </summary>
        //private ServicioBD mBaseDeDatos;

        /// <summary>
        /// Indica si se envían los correos
        /// </summary>
        private bool mEnviarCorreos;

        /// <summary>
        /// Hora en ticks de la última carga de las notificaciones
        /// </summary>
        private long mHoraUltimaCarga = DateTime.Now.Ticks;

        /// <summary>
        /// Intervalo de repetición de carga de notificaciones fallidas
        /// </summary>
        private long mIntervalo = 3000000000; //5 minutos

        /// <summary>
        /// Intervalo de repetición de carga de notificaciones fallidas
        /// </summary>
        private long mIntervaloCancelacion = 864000000000; //24 horas

        /// <summary>
        /// Lista con los Smtp StatusCode que serán notificables al usuario
        /// </summary>
        private List<SmtpStatusCode> mErroresNotificables = new List<SmtpStatusCode> { SmtpStatusCode.MailboxUnavailable };

        /// <summary>
        /// Indica el nombre del ecosistema
        /// </summary>
        private string mNombreEcosistema;

        /// <summary>
        /// Correo de sugerencias almacenado en parámetro aplicación
        /// </summary>
        private string mCorreoSugerencias;

        /// <summary>
        /// Guid con el proyecto id del ecosistema.
        /// </summary>
        private Guid? mProyectoPrincipalUnico;

        /// <summary>
        /// Diccionario que contiene las urls propias de los proyectos
        /// </summary>
        private Dictionary<Guid, string> mUrlsPropiasProyectos;

        /// <summary>
        /// Obtiene si se trata de un ecosistema sin metaproyecto
        /// </summary>
        private bool? mEsEcosistemaSinMetaProyecto = null;

        private LogStatus mEstadoProceso = LogStatus.Correcto;

        private Dictionary<Guid, GestorParametroGeneral> mDiccionarioParametroGralPorProyecto = new Dictionary<Guid, GestorParametroGeneral>();

        public Dictionary<Guid, ConfiguracionEnvioCorreo> mListaConfiguracionEnvioCorreo;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor a partir de la base de datos pasada por parámetro
        /// </summary>
        /// <param name="pBaseDeDatos">Base de datos</param>
        public NotificacionController(IServiceScopeFactory serviceScope, ConfigService configService, int sleep = 0)
            : base(serviceScope, configService)
        {
            mListaConfiguracionEnvioCorreo = new Dictionary<Guid, ConfiguracionEnvioCorreo>();
        }

        protected override ControladorServicioGnoss ClonarControlador()
        {
            return new NotificacionController(ScopedFactory, mConfigService);
        }

        #endregion

        #region Métodos

        #region Públicos

        private void RealizarMantenimientoRabbitMQ(LoggingService pLoginService, bool reintentar = true)
        {
            RabbitMQClient.ReceivedDelegate funcionProcesarItem = new RabbitMQClient.ReceivedDelegate(ProcesarItem);
            RabbitMQClient.ShutDownDelegate funcionShutDown = new RabbitMQClient.ShutDownDelegate(OnShutDown);

            RabbitMqClientLectura = new RabbitMQClient(RabbitMQClient.BD_SERVICIOS_WIN, COLA_NOTIFICACION, pLoginService, mConfigService, EXCHANGE, COLA_NOTIFICACION);

            try
            {
                RabbitMqClientLectura.ObtenerElementosDeCola(funcionProcesarItem, funcionShutDown);
                mReiniciarLecturaRabbit = false;
            }
            catch (Exception ex)
            {
                mReiniciarLecturaRabbit = true;
                pLoginService.GuardarLogError(ex);
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
                ConfigService configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
                IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication = scope.ServiceProvider.GetRequiredService<IServicesUtilVirtuosoAndReplication>();
                ComprobarTraza("Mail", entityContext, loggingService, redisCacheWrapper, configService, servicesUtilVirtuosoAndReplication);
                try
                {
                    ComprobarCancelacionHilo();

                    Debug.WriteLine($"ProcesarItem, {pFila}!");

                    if (!string.IsNullOrEmpty(pFila))
                    {
                        Guid notificacionID = JsonConvert.DeserializeObject<Guid>(pFila);

                        AD.EntityModel.Models.Notificacion.Notificacion notificacion = entityContext.Notificacion.Where(item => item.NotificacionID.Equals(notificacionID)).FirstOrDefault();

                        NotificacionCN notificacionCN = new NotificacionCN(entityContext, loggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                        GestionNotificaciones gestionNotificaciones = new GestionNotificaciones(notificacionCN.ObtenerEnvioNotificacionesRabbitMQ(notificacionID), loggingService, entityContext, mConfigService, servicesUtilVirtuosoAndReplication);

                        ProcesarFilaNotificacion(new Notificacion(notificacion, gestionNotificaciones, loggingService), entityContext, loggingService, servicesUtilVirtuosoAndReplication);

                        //Guardo DataSet en BD física
                        notificacionCN.ActualizarNotificacion();

                        ControladorConexiones.CerrarConexiones(false);
                    }

                    return true;
                }
                catch (GnossSmtpException ex)
                {
                    loggingService.GuardarLogError(ex);
                    return true;
                }
                catch (Exception ex)
                {
                    // Ha habido un error no relacionado con el servidor SMTP, no marcamos la fila como procesada
                    loggingService.GuardarLogError(ex);
                    return false;
                }
                finally
                {
                    GuardarTraza(loggingService);
                }
            }
        }

        public void RealizarMantenimientoBD(LoggingService pLoggingService, EntityContext pEntityContext, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            mEnviarCorreos = true;

            LogStatus estadoProceso = LogStatus.Error;
            string entradaLog = string.Empty;
            GestionNotificaciones gestorNotificaciones = null;

            while (mEnviarCorreos)
            {
                try
                {
                    ComprobarCancelacionHilo();

                    if (mReiniciarLecturaRabbit)
                    {
                        RealizarMantenimientoRabbitMQ(pLoggingService);
                    }

                    //(Re)Carga los datos de la BD referentes a notificaciones
                    gestorNotificaciones = CargarDatos(pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);

                    //Envio y Log Notificaciones
                    estadoProceso = EnviarNotificaciones(gestorNotificaciones, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);

                    switch (estadoProceso)
                    {
                        case LogStatus.Correcto:
                            entradaLog = LogStatus.Enviando.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(estadoProceso, "Todos los mensajes enviados");
                            break;
                        case LogStatus.Error:
                            entradaLog = LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(estadoProceso, "Hay mensajes que no se han podido enviar");
                            break;
                    }

                    if (!estadoProceso.Equals(LogStatus.NoEnviado))
                    {
                        //Escribe entrada en Log
                        pLoggingService.GuardarLog(entradaLog);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SmtpException smtpEx)
                {
                    //La configuración del buzon de correo (cuenta, usuario, password) es erronea, se escribe en el log y se detiene el servicio.
                    pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(LogStatus.Error, "La configuración del buzón (dirección de correo, usuario y contraseña) no es válida: " + smtpEx.Message + " : " + smtpEx.StackTrace));

                    //Recogemos el innerexception antes de lanzar el nuevo exception:
                    if (smtpEx.InnerException != null)
                    {
                        pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") InnerExceptionMessage: " + this.CrearEntradaRegistro(LogStatus.Error, smtpEx.InnerException.Message) + " InnerExceptionStackTrace: " + smtpEx.InnerException.StackTrace);
                    }
                }
                catch (Exception ex)
                {
                    pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(LogStatus.Error, ex.Message));

                    //Recogemos el innerexception
                    if (ex.InnerException != null)
                    {
                        pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") InnerExceptionMessage: " + this.CrearEntradaRegistro(LogStatus.Error, ex.InnerException.Message) + " InnerException StackTrace: " + ex.InnerException.StackTrace);
                    }
                }
                finally
                {
                    try
                    {
                        NotificacionCN notificacionCN = new NotificacionCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                        //Guardo DataSet en BD física
                        notificacionCN.ActualizarNotificacion();

                        notificacionCN.Dispose();
                        if (gestorNotificaciones != null)
                        {
                            gestorNotificaciones.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        pLoggingService.GuardarLog(ex.Message);
                    }

                    //Duermo el proceso el tiempo establecido
                    Thread.Sleep(NotificacionController.INTERVALO_SEGUNDOS * 1000);
                }
            }
        }

        /// <summary>
        /// Realiza el envio de las notificaciones pendientes de enviar y escribe en el fichero de log una entrada 
        /// indicando el resultado de la operación
        /// </summary>
        public override void RealizarMantenimiento(EntityContext entityContext, EntityContextBASE entityContextBASE, UtilidadesVirtuoso utilidadesVirtuoso, LoggingService logginService, RedisCacheWrapper redisCacheWrapper, GnossCache gnossCache, VirtuosoAD virtuosoAD, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            if (mConfigService.ExistRabbitConnection(RabbitMQClient.BD_SERVICIOS_WIN))
            {
                RealizarMantenimientoRabbitMQ(logginService);
            }
            else
            {
                RealizarMantenimientoBD(logginService, entityContext, servicesUtilVirtuosoAndReplication);
            }
        }

        #endregion

        #region Privados

        private string CargarIdioma(AD.EntityModel.Models.Notificacion.NotificacionCorreoPersona notificacionCorreo, PersonaCN persCN)
        {
            string idioma = "";

            if (notificacionCorreo.PersonaID.HasValue)
            {
                idioma = persCN.ObtenerIdiomaDePersonaID(notificacionCorreo.PersonaID.Value);
            }
            if (idioma == "")
            {
                if (notificacionCorreo.Notificacion.Idioma != null && notificacionCorreo.Notificacion.Idioma.Trim() != "")
                {
                    idioma = notificacionCorreo.Notificacion.Idioma;
                }
                else
                {
                    idioma = "es";
                }
            }

            return idioma;
        }

        private string MontarAsunto(string pIdioma, short pMensajeID, GestorParametroGeneral pParametroGeneralDS, string pUrlProyecto, string pNombreProyecto, string pUrlContent, Notificacion pNotificacion)
        {
            string asunto = ObtenerTextoParteMensaje($"mensajeid={pMensajeID.ToString()}/asunto", pParametroGeneralDS, pIdioma);

            if (string.IsNullOrEmpty(asunto))
            {
                asunto = pNotificacion.GestorNotificaciones.ListaMensajes(pIdioma)[pMensajeID].Key;
            }
            asunto = SustituirParametrosMensajes(asunto, pUrlProyecto, pNombreProyecto, pUrlContent);
            return asunto;
        }

        private string MontarMensaje(string pIdioma, short pMensajeID, GestorParametroGeneral pParametroGeneralDS, string pUrlProyecto, string pNombreProyecto, string pUrlContent, Notificacion pNotificacion)
        {
            string mensaje = ObtenerTextoParteMensaje($"mensajeid={pMensajeID.ToString()}/texto", pParametroGeneralDS, pIdioma);

            if (string.IsNullOrEmpty(mensaje))
            {
                mensaje = pNotificacion.GestorNotificaciones.ListaMensajes(pIdioma)[pMensajeID].Value;
            }
            mensaje = SustituirParametrosMensajes(mensaje, pUrlProyecto, pNombreProyecto, pUrlContent);
            return mensaje;
        }

        private string MontarCabecera(string pIdioma, Notificacion notificacion, short tipoProyecto, string urlBaseProyecto, string nombreProyecto, GestorParametroGeneral pParametroGeneralDS, string pUrlProyecto, string pUrlContent, string urlStatic, UtilIdiomas pUtilIdiomas, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            string cabecera = ObtenerTextoParteMensaje("cabecera", pParametroGeneralDS, pUtilIdiomas.LanguageCode);

            if (string.IsNullOrEmpty(cabecera))
            {
                cabecera = notificacion.GestorNotificaciones.ListaFormatosCorreo(pIdioma)["cabecera"];
            }

            if (notificacion.FilaNotificacion.ProyectoID.HasValue && notificacion.FilaNotificacion.ProyectoID != ProyectoAD.MetaProyecto && notificacion.FilaNotificacion.ProyectoID != Guid.Empty)
            {
                if ((tipoProyecto == (short)TipoProyecto.Universidad20 || tipoProyecto == (short)TipoProyecto.EducacionExpandida || tipoProyecto == (short)TipoProyecto.EducacionPrimaria) && !ProyectoPrincipalUnico.Equals(Guid.Empty) && ProyectoPrincipalUnico != ProyectoAD.MetaProyecto)
                {
                    cabecera = MontarCabeceraProyectoID(cabecera, pUrlContent, nombreProyecto, ProyectoPrincipalUnico, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
                }
                else
                {
                    cabecera = MontarCabeceraProyectoID(cabecera, pUrlContent, nombreProyecto, notificacion.FilaNotificacion.ProyectoID.Value, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
                }
            }
            else if (ProyectoPrincipalUnico != ProyectoAD.MetaProyecto)
            {
                cabecera = MontarCabeceraProyectoID(cabecera, pUrlContent, nombreProyecto, ProyectoPrincipalUnico, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
            }
            else
            {
                cabecera = cabecera.Replace("IMAGENPROYECTO", "<img src=\"https://static.gnoss.ws/img/logognossazul.png\">");
            }

            cabecera = SustituirParametrosMensajes(cabecera, pUrlProyecto, nombreProyecto, pUrlContent);

            return cabecera;
        }

        private string MontarCabeceraProyectoID(string pCabecera, string pUrlContent, string pNombreProyecto, Guid pProyectoID, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            string cabecera = pCabecera;
            ParametroGeneralCN paramGeneralCN = new ParametroGeneralCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            ParametroGeneral filaParamGeneral = paramGeneralCN.ObtenerFilaParametrosGeneralesDeProyecto(pProyectoID);

            if (!string.IsNullOrEmpty(filaParamGeneral.CoordenadasSup))
            {
                string v = "";
                if (filaParamGeneral.VersionFotoImagenSupGrande.HasValue)
                {
                    v = "?" + filaParamGeneral.VersionFotoImagenSupGrande;
                }
                cabecera = cabecera.Replace("IMAGENPROYECTO", "<img src=\"" + pUrlContent + "/Imagenes/Proyectos/" + pProyectoID.ToString() + ".png" + v + "\">");

            }
            else
            {
                cabecera = cabecera.Replace("IMAGENPROYECTO", "<p style=\"font-size:30px;color:#8186BD;\">" + pNombreProyecto + "</p>");
            }

            return cabecera;
        }

        private string MontarPie(string pIdioma, Notificacion notificacion, UtilIdiomas utilIdiomas, string urlBaseProyecto, string urlProyecto, string nombreProyecto, string nombrecortoComunidad, GestorParametroGeneral pParametroGeneralDS, string pUrlContent)
        {
            string pie = ObtenerTextoParteMensaje("pie", pParametroGeneralDS, utilIdiomas.LanguageCode);

            if (string.IsNullOrEmpty(pie))
            {
                pie = notificacion.GestorNotificaciones.ListaFormatosCorreo(pIdioma)["pie"];
            }

            if (AgnadirColetillaNotificacionPerfil(notificacion.FilaNotificacion.MensajeID))
            {
                pie = ObtenerTextoParteMensaje("pieNotificaciones", pParametroGeneralDS, utilIdiomas.LanguageCode);

                if (string.IsNullOrEmpty(pie))
                {
                    pie = notificacion.GestorNotificaciones.ListaFormatosCorreo(pIdioma)["pieNotificaciones"];
                }
            }

            if (pie.Contains("<#URLCOMUNIDAD#>") || pie.Contains("<#NOMBRECOMUNIDAD#>") || pie.Contains("<#POLITICAPRIVACIDAD#>") || pie.Contains("<#CONDICIONESUSO#>") || pie.Contains("<#URCONNOMBRECOMUNIDAD#>") || pie.Contains("<#SECCIONNOTIFICACIONES#>"))
            {

                string URCONNOMBRECOMUNIDAD = utilIdiomas.GetText("METABUSCADOR", "TODASCOMUNIDADES");
                string SECCIONNOTIFICACIONES = "<a href=\"" + urlBaseProyecto + "/editar-perfil-notificacion\">" + utilIdiomas.GetText("SUSCRIPCIONES", "SECCIONNOTIFICACIONPERFIL") + "</a>";

                if (!string.IsNullOrEmpty(nombrecortoComunidad))
                {
                    if (notificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.BoletinSuscripcion)
                    {
                        URCONNOMBRECOMUNIDAD = "<a href=\"" + urlProyecto + "\">" + nombreProyecto + "</a>";
                        SECCIONNOTIFICACIONES = "<a href=\"" + urlProyecto + "/" + utilIdiomas.GetText("URLSEM", "ADMINISTRARSUSCRIPCIONCOMUNIDAD") + "\">" + utilIdiomas.GetText("SUSCRIPCIONES", "SECCIONSUSCRIBETECOMUNIDAD", nombreProyecto) + "</a>";
                    }
                }

                pie = pie.Replace("<#URCONNOMBRECOMUNIDAD#>", URCONNOMBRECOMUNIDAD);
                pie = pie.Replace("<#SECCIONNOTIFICACIONES#>", SECCIONNOTIFICACIONES);
                pie = pie.Replace("<#POLITICAPRIVACIDAD#>", urlProyecto + "/" + utilIdiomas.GetText("URLSEM", "POLITICAPRIVACIDAD"));
                pie = pie.Replace("<#CONDICIONESUSO#>", urlProyecto + "/" + utilIdiomas.GetText("URLSEM", "CONDICIONESUSO"));
            }
            pie = SustituirParametrosMensajes(pie, urlProyecto, nombreProyecto, pUrlContent);

            return pie;
        }

        /// <summary>
        /// Obtiene el texto del nodo correspondiente del mensaje
        /// </summary>
        /// <param name="pParteMensaje">Nodo del archivo de idioma del mensaje que se quiere recuperar</param>
        /// <param name="pParametroGeneralDS"></param>
        /// <returns></returns>
        private string ObtenerTextoParteMensaje(string pParteMensaje, GestorParametroGeneral pParametroGeneralDS, string pIdioma)
        {
            string texto = string.Empty;

            if (pParametroGeneralDS != null && pParametroGeneralDS.ListaTextosPersonalizadosProyecto.Count > 0)
            {
                List<TextosPersonalizadosProyecto> filasTextos = pParametroGeneralDS.ListaTextosPersonalizadosProyecto.Where(textosPersonalizadosProyecto => textosPersonalizadosProyecto.TextoID.Equals(pParteMensaje) && textosPersonalizadosProyecto.Language.Equals(pIdioma)).ToList();

                if (filasTextos.Count > 0)
                {
                    //Si existe en el idioma solicitado lo cogemos en el idioma
                    texto = filasTextos[0].Texto;
                }
                else
                {
                    filasTextos = pParametroGeneralDS.ListaTextosPersonalizadosProyecto.Where(textoPesonalizado => textoPesonalizado.TextoID.Equals(pParteMensaje)).ToList();
                    if (filasTextos.Count > 0)
                    {
                        //Si no existe en el idioma seleccionado pero si que existe en otro lo mandamos en otro idioma
                        texto = filasTextos[0].Texto;
                    }
                }
            }

            if (string.IsNullOrEmpty(texto) && GestorParametroAplicacionDS != null && GestorParametroAplicacionDS.ListaTextosPersonalizadosPlataforma.Count > 0)
            {
                List<TextosPersonalizadosPlataforma> filasTextos = GestorParametroAplicacionDS.ListaTextosPersonalizadosPlataforma.Where(textoPersonalizadoPlataforma => textoPersonalizadoPlataforma.TextoID.Equals(pParteMensaje) && textoPersonalizadoPlataforma.Language.Equals(pIdioma)).ToList();

                if (filasTextos.Count > 0)
                {
                    //Si existe en el idioma solicitado lo cogemos en el idioma
                    texto = filasTextos[0].Texto;
                }
                else
                {
                    filasTextos = GestorParametroAplicacionDS.ListaTextosPersonalizadosPlataforma.Where(textoPersonalizadoPlat => textoPersonalizadoPlat.TextoID.Equals(pParteMensaje)).ToList();
                    if (filasTextos.Count > 0)
                    {
                        //Si no existe en el idioma seleccionado pero si que existe en otro lo mandamos en otro idioma
                        texto = filasTextos[0].Texto;
                    }
                }
            }

            //En los mensajes hay que poner '{' y '}' para que se sustituyan por '<' y '>' respectivamente
            //Si en el mensaje se quiere poner '{' y '}' lo que hay que poner es '\\{' y '\\}' respectivamente
            string textoAux1 = "|@#|@#|@#<";
            string textoAux2 = "|@#|@#|@#>";
            texto = texto.Replace("\\\\{", textoAux1).Replace("\\\\}", textoAux2);
            texto = texto.Replace('{', '<').Replace('}', '>');
            texto = texto.Replace(textoAux1, "{").Replace(textoAux2, "}");

            return texto;
        }

        private string SustituirParametrosMensajes(string pTexto, string pUrlProyecto, string pNombreComunidad, string pUrlContent)
        {
            pTexto = pTexto.Replace("<#NOMBRECOMUNIDAD#>", pNombreComunidad);
            pTexto = pTexto.Replace("<#URLCOMUNIDAD#>", pUrlProyecto);
            pTexto = pTexto.Replace("<#URLCONTENT#>", pUrlContent);
            return pTexto;
        }

        private ICorreo ObtenerGestorCorreoDeProyecto(Guid pProyectoID, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            ConfiguracionEnvioCorreo parametros = ObtenerParametrosConfiguracionEnvioCorreo(pProyectoID, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);

            if (parametros == null)
            {
                throw new Exception("No Existe la configuración de envio de correo en la Base De Datos, Consulta la tabla 'ConfiguracionEnvioCorreo'");
            }
            if (parametros.tipo.ToLower().Equals("smtp"))
            {
                return new UtilCorreo(parametros.smtp, (int)parametros.puerto, parametros.usuario, parametros.clave, parametros.SSL);
            }
            else
            {
                pLoggingService.GuardarLogError($"Usuario: {parametros.usuario}, Contrasena: {parametros.clave}, SMTP: {parametros.smtp}");
                return new UtilEws(parametros.usuario, parametros.clave, parametros.smtp);
            }
        }

        private ConfiguracionEnvioCorreo ObtenerParametrosConfiguracionEnvioCorreo(Guid pProyectoID, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            ConfiguracionEnvioCorreo parametros = null;

            string ficheroConexion = mFicheroConfiguracionBD;

            if (mListaConfiguracionEnvioCorreo.ContainsKey(pProyectoID))
            {
                parametros = mListaConfiguracionEnvioCorreo[pProyectoID];
            }
            else
            {
                ParametroCN parametroCN = new ParametroCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                parametros = parametroCN.ObtenerConfiguracionEnvioCorreo(pProyectoID);
                parametroCN.Dispose();
                mListaConfiguracionEnvioCorreo.Add(pProyectoID, parametros);
            }

            if (parametros == null && pProyectoID != ProyectoAD.MetaProyecto)
            {
                parametros = ObtenerParametrosConfiguracionEnvioCorreo(ProyectoAD.MetaProyecto, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
            }

            return parametros;
        }

        /// <summary>
        /// Realiza el envío de las notificaciones
        /// </summary>
        /// <returns>Estado del resultado de la operacion del envio de las notificaciones</returns>
        private LogStatus EnviarNotificaciones(GestionNotificaciones pGestorNotificaciones, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            pGestorNotificaciones.CargarGestor();

            if (pGestorNotificaciones.ListaNotificaciones.Values.Count > 0)
            {
                foreach (Notificacion notificacion in pGestorNotificaciones.ListaNotificaciones.Values)
                {
                    ProcesarFilaNotificacion(notificacion, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
                }
                mDiccionarioParametroGralPorProyecto.Clear();
            }
            else
            {
                mEstadoProceso = LogStatus.NoEnviado;
            }
            return mEstadoProceso;
        }

        private void ProcesarFilaNotificacion(Notificacion pNotificacion, EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            PersonaCN persCN = new PersonaCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            ProyectoCN proyCN = new ProyectoCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);

            AD.EntityModel.Models.Notificacion.NotificacionCorreoPersona notificacionCorreo = pNotificacion.FilaNotificacion.NotificacionCorreoPersona.FirstOrDefault();

            if (notificacionCorreo != null)
            {
                try
                {
                    AD.EntityModel.Models.PersonaDS.ConfiguracionGnossPersona filaConfig = null;

                    if (notificacionCorreo.PersonaID.HasValue)
                    {
                        filaConfig = persCN.ObtenerConfiguracionPersonaPorID(notificacionCorreo.PersonaID.Value);
                    }
                    if (filaConfig == null || ComprobarPermisos(filaConfig, pNotificacion.FilaNotificacion.MensajeID))
                    {
                        Guid proyectoID = ProyectoAD.MetaProyecto;

                        if (pNotificacion.FilaNotificacion.ProyectoID.HasValue && pNotificacion.FilaNotificacion.ProyectoID != ProyectoAD.MyGnoss && pNotificacion.FilaNotificacion.ProyectoID != Guid.Empty)
                        {
                            proyectoID = pNotificacion.FilaNotificacion.ProyectoID.Value;
                        }
                        ParametroGeneral filaParametros = null;
                        if (!mDiccionarioParametroGralPorProyecto.ContainsKey(proyectoID))
                        {
                            ParametroGeneralGBD gestorParametroGeneralController = new ParametroGeneralGBD(pEntityContext);
                            GestorParametroGeneral gestorParametroGeneral = new GestorParametroGeneral();
                            gestorParametroGeneralController.ObtenerParametrosGeneralesDeProyectoConIdiomas(gestorParametroGeneral, proyectoID);
                            mDiccionarioParametroGralPorProyecto.Add(proyectoID, gestorParametroGeneral);

                        }

                        ICorreo gestorCorreo = ObtenerGestorCorreoDeProyecto(proyectoID, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);
                        string idioma = CargarIdioma(notificacionCorreo, persCN);


                        Proyecto filaProyecto = proyCN.ObtenerProyectoPorIDCargaLigera(proyectoID);

                        if (filaParametros == null)
                        {
                            filaParametros = mDiccionarioParametroGralPorProyecto[proyectoID].ListaParametroGeneral.FirstOrDefault();
                        }

                        short tipoProyecto = filaProyecto.TipoProyecto;

                        string nombreProyecto = UtilCadenas.ObtenerTextoDeIdioma(filaProyecto.Nombre, idioma, filaParametros.IdiomaDefecto);
                        string urlBaseProyecto = UtilCadenas.ObtenerTextoDeIdioma(filaProyecto.URLPropia, idioma, filaParametros.IdiomaDefecto);

                        string nombrecortoComunidad = "";
                        if (proyectoID != ProyectoAD.MetaProyecto)
                        {
                            nombrecortoComunidad = filaProyecto.NombreCorto;
                        }

                        UtilIdiomas utilIdiomas = new UtilIdiomas(idioma, pLoggingService, pEntityContext, mConfigService);

                        string urlProyecto = urlBaseProyecto;
                        if (idioma != "es")
                        {
                            urlBaseProyecto += $"/{idioma}";
                            urlProyecto += "/" + idioma;

                            ParametroCN parametroCN = new ParametroCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                            Dictionary<string, string> listaParametros = parametroCN.ObtenerParametrosProyecto(proyectoID);
                            bool proyectoSinURL = listaParametros.ContainsKey(ParametroAD.ProyectoSinNombreCortoEnURL) && listaParametros[ParametroAD.ProyectoSinNombreCortoEnURL] == "1";

                            if (!proyectoSinURL && !string.IsNullOrEmpty(nombrecortoComunidad))
                            {
                                urlProyecto += $"/{nombrecortoComunidad}";
                            }
                        }

                        string dominio = urlBaseProyecto.Replace("http://", "").Replace("https://", "");
                        string urlContent = UrlContent(proyectoID, dominio, mConfigService);

                        if (!string.IsNullOrEmpty(urlContent))
                        {
                            string[] listaURL = urlContent.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string url in listaURL)
                            {
                                urlContent = url;
                                break;
                            }
                        }
                        else
                        {
                            urlContent = urlBaseProyecto;
                        }

                        string urlStatic = BaseURLStatic(proyectoID, dominio, mConfigService);

                        if (string.IsNullOrEmpty(urlStatic))
                        {
                            urlStatic = urlBaseProyecto;
                        }

                        string cabecera = MontarCabecera(idioma, pNotificacion, tipoProyecto, urlStatic, nombreProyecto, mDiccionarioParametroGralPorProyecto[proyectoID], urlProyecto, urlContent, urlStatic, utilIdiomas, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication);

                        string pie = MontarPie(idioma, pNotificacion, utilIdiomas, urlBaseProyecto, urlProyecto, nombreProyecto, nombrecortoComunidad, mDiccionarioParametroGralPorProyecto[proyectoID], urlContent);

                        string MiAsunto = MontarAsunto(idioma, notificacionCorreo.Notificacion.MensajeID, mDiccionarioParametroGralPorProyecto[proyectoID], urlProyecto, nombreProyecto, urlContent, pNotificacion);

                        string MiMensaje = MontarMensaje(idioma, notificacionCorreo.Notificacion.MensajeID, mDiccionarioParametroGralPorProyecto[proyectoID], urlProyecto, nombreProyecto, urlContent, pNotificacion);

                        string paramIdentidad = "<$identidad$>";

                        if (MiAsunto.Contains(paramIdentidad) || MiMensaje.Contains(paramIdentidad))
                        {
                            string identidad = "";

                            if (notificacionCorreo.OrganizacionPersonaID.HasValue)
                            {

                                OrganizacionCN orgCN = new OrganizacionCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                                string nombreCortoOrg = orgCN.ObtenerNombreOrganizacionPorID(notificacionCorreo.OrganizacionPersonaID.Value).NombreCorto;
                                orgCN.Dispose();
                                identidad = "/" + utilIdiomas.GetText("URLSEM", "IDENTIDAD") + "/" + nombreCortoOrg;
                            }

                            if (MiAsunto.Contains(paramIdentidad))
                            {
                                MiAsunto = MiAsunto.Replace(paramIdentidad, identidad);
                            }

                            if (MiMensaje.Contains(paramIdentidad))
                            {
                                MiMensaje = MiMensaje.Replace(paramIdentidad, identidad);
                            }
                        }

                        Dictionary<string, string> mListaParametros = new Dictionary<string, string>();

                        foreach (AD.EntityModel.Models.Notificacion.NotificacionParametro filaParametro in pNotificacion.FilaNotificacion.NotificacionParametro.ToList())
                        {
                            string nombreParametro = GestionNotificaciones.TextoDeParametro(filaParametro.ParametroID);
                            string valor = filaParametro.Valor;
                            mListaParametros.Add(nombreParametro, valor);
                        }

                        foreach (string parametro in mListaParametros.Keys)
                        {
                            if (parametro != GestionNotificaciones.TextoDeParametro((short)ClavesParametro.LogoGnoss))
                            {
                                MiMensaje = MiMensaje.Replace(parametro, mListaParametros[parametro]);
                                MiAsunto = MiAsunto.Replace(parametro, mListaParametros[parametro]);
                            }
                        }

                        //Si falta el nombre del proyecto, lo ponemos
                        if (MiMensaje.Contains("<#NombreProyecto#>") || MiAsunto.Contains("<#NombreProyecto#>"))
                        {
                            MiMensaje = MiMensaje.Replace("<#NombreProyecto#>", nombreProyecto);
                            MiAsunto = MiAsunto.Replace("<#NombreProyecto#>", nombreProyecto);
                        }

                        //Si falta el nombre del ecosistema, lo ponemos
                        if (MiMensaje.Contains("<#NombreEcosistema#>") || MiAsunto.Contains("<#NombreEcosistema#>"))
                        {
                            if (string.IsNullOrEmpty(mNombreEcosistema))
                            {
                                Proyecto filaMetaProyecto = proyCN.ObtenerProyectoCargaLigeraPorID(ProyectoAD.MetaProyecto).ListaProyecto.FirstOrDefault();
                                mNombreEcosistema = filaMetaProyecto.Nombre;
                            }

                            MiMensaje = MiMensaje.Replace("<#NombreEcosistema#>", mNombreEcosistema);
                            MiAsunto = MiAsunto.Replace("<#NombreEcosistema#>", mNombreEcosistema);
                        }

                        //Si falta la UrlIntragnossBBDD, la ponemos
                        if (MiMensaje.Contains("<#UrlIntragnossBBDD#>") || MiAsunto.Contains("<#UrlIntragnossBBDD#>"))
                        {
                            string urlPropia = "";
                            if (!UrlsPropiasProyectos.ContainsKey(proyectoID))
                            {
                                if (EsEcosistemaSinMetaProyecto)
                                {
                                    urlPropia = proyCN.ObtenerURLPropiaProyecto(proyectoID);
                                }
                                else
                                {
                                    urlPropia = proyCN.ObtenerURLPropiaProyecto(ProyectoAD.MetaProyecto);
                                }

                                if (!urlPropia.EndsWith("/"))
                                {
                                    urlPropia += "/";
                                }

                                UrlsPropiasProyectos.Add(proyectoID, urlPropia);
                            }
                            else
                            {
                                urlPropia = UrlsPropiasProyectos[proyectoID];
                            }

                            MiMensaje = MiMensaje.Replace("<#UrlIntragnossBBDD#>", urlPropia);
                            MiAsunto = MiAsunto.Replace("<#UrlIntragnossBBDD#>", urlPropia);
                        }

                        //Si falta la UrlIntragnoss, la ponemos
                        if (MiMensaje.Contains("<@UrlIntragnoss@>") || MiAsunto.Contains("<@UrlIntragnoss@>"))
                        {
                            string urlPropia = "";
                            if (!UrlsPropiasProyectos.ContainsKey(proyectoID))
                            {
                                if (EsEcosistemaSinMetaProyecto)
                                {
                                    urlPropia = proyCN.ObtenerURLPropiaProyecto(proyectoID);
                                }
                                else
                                {
                                    urlPropia = proyCN.ObtenerURLPropiaProyecto(ProyectoAD.MetaProyecto);
                                }

                                if (!urlPropia.EndsWith("/"))
                                {
                                    urlPropia += "/";
                                }

                                UrlsPropiasProyectos.Add(proyectoID, urlPropia);
                            }
                            else
                            {
                                urlPropia = UrlsPropiasProyectos[proyectoID];
                            }

                            string parametroReemplazar = "<@UrlIntragnoss@>";
                            if (urlPropia.Contains("@"))
                            {
                                if (urlPropia.Contains("|||"))
                                {
                                    urlPropia = urlPropia.Substring(0, urlPropia.IndexOf("|||"));
                                }
                                string language = urlPropia.Substring(urlPropia.IndexOf("@") + 1);
                                urlPropia = urlPropia.Substring(0, urlPropia.IndexOf("@"));

                                if (!language.Equals(utilIdiomas.LanguageCode) && utilIdiomas.LanguageCode.Equals("es"))
                                {
                                    // El lenguaje por defecto no es el español, añado /es a la url para que funcionen todas las URLs en español
                                    urlPropia += "/es";
                                }
                                else if (language.Equals(utilIdiomas.LanguageCode) && !utilIdiomas.LanguageCode.Equals("es"))
                                {
                                    // El lenguaje por defecto no es el español, quito el lenguaje de la url ya que no hace falta, porque es el de por defecto. 
                                    MiMensaje = MiMensaje.Replace($"{parametroReemplazar}/{language}", urlPropia);
                                    MiAsunto = MiAsunto.Replace($"{parametroReemplazar}/{language}", urlPropia);
                                }
                            }
                            //Evitar la doble barra (//) a la hora de enviar un correo con el enlace para responder el mensaje
                            string[] partesHref = MiMensaje.Split("href=\"");
                            if(partesHref.Length == 2)
                            {
                                int tamanioEnlace = partesHref[1].IndexOf('"');
                                string enlace = partesHref[1].Substring(0,tamanioEnlace);
                                if (enlace.StartsWith(parametroReemplazar))
                                {
                                    int parametroRemplazarLongitud = parametroReemplazar.Length;
                                    string afterIntragnoss = enlace.Substring(parametroRemplazarLongitud);
                                    if (afterIntragnoss.StartsWith('/') && urlPropia.EndsWith('/'))
                                    {
                                        urlPropia = urlPropia.TrimEnd('/');
                                    }
                                }
                            }
                            //FIN evitar doble barra

                            MiMensaje = MiMensaje.Replace(parametroReemplazar, urlPropia);
                            MiAsunto = MiAsunto.Replace(parametroReemplazar, urlPropia);
                        }

                        //Si falta la CorreoSugerencias, la ponemos
                        if (MiMensaje.Contains("<#CorreoSugerencias#>") || MiAsunto.Contains("<#CorreoSugerencias#>"))
                        {
                            if (string.IsNullOrEmpty(mCorreoSugerencias))
                            {
                                ParametroAplicacionCN paramApliCN = new ParametroAplicacionCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
                                mCorreoSugerencias = paramApliCN.ObtenerCorreoSugerencias();
                                paramApliCN.Dispose();
                            }

                            MiMensaje = MiMensaje.Replace("<#CorreoSugerencias#>", mCorreoSugerencias);
                            MiAsunto = MiAsunto.Replace("<#CorreoSugerencias#>", mCorreoSugerencias);
                        }

                        //Obtenemos el correo de la persona en la organizacion si tenemos los datos necesarios, 
                        //sino utilizamos el de la notificacion
                        string email = "";

                        if (notificacionCorreo.PersonaID.HasValue)
                        {
                            if (!notificacionCorreo.OrganizacionPersonaID.HasValue)
                            {
                                email = persCN.ObtenerCorreoDePersonaID(notificacionCorreo.PersonaID.Value, null);
                            }
                            else
                            {
                                email = persCN.ObtenerCorreoDePersonaID(notificacionCorreo.PersonaID.Value, notificacionCorreo.OrganizacionPersonaID);
                            }
                        }
                        if (email == null || email == "")
                        {
                            email = notificacionCorreo.EmailEnvio;
                        }

                        string remitente = nombreProyecto;
                        if (pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.AvisoCorreoNuevoContacto || pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.NotificacionEnlaceExterno || pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.ComentarioDocumentoCorreo || pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.AvisoCorreoBienvenidaProyecto)
                        {
                            if (mListaParametros.ContainsKey("<#NombreRemitente#>"))
                            {
                                remitente = utilIdiomas.GetText("BANDEJAENTRADA", "REMITENTEGNOSS", mListaParametros["<#NombreRemitente#>"], nombreProyecto);
                            }
                        }
                        else if (pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.SolicitudNuevoUsuarioCorporateExcellence)
                        {
                            remitente = UtilCorreo.MASCARA_SOLICITUD_CORPORATE_EXCELLENCE;
                        }
                        else if (pNotificacion.FilaNotificacion.MensajeID == (short)TiposNotificacion.BoletinSuscripcion)
                        {
                            if (mListaParametros.ContainsKey("<#NombreProyecto#>"))
                            {
                                remitente = mListaParametros["<#NombreProyecto#>"];
                            }
                        }
                        else if (pNotificacion.FilaNotificacion.ProyectoID.HasValue && pNotificacion.FilaNotificacion.ProyectoID != ProyectoAD.MyGnoss && pNotificacion.FilaNotificacion.ProyectoID != Guid.Empty)
                        {
                            remitente = nombreProyecto;
                        }

                        bool tieneParametrosSinCompletar = Regex.IsMatch(MiAsunto, "(<#.+#>|<@.+@>)") || Regex.IsMatch(MiMensaje, "(<#.+#>|<@.+@>)");
                        if (tieneParametrosSinCompletar)
                        {
                            pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(LogStatus.Error, "No se ha enviado la notificación " + pNotificacion.Clave + " porque faltan parámetros: Asunto:" + MiAsunto + "Mensaje" + MiMensaje));
                            //Si falta algún parametro no se manda
                            if (pNotificacion.FilaNotificacion.FechaNotificacion.AddHours(1) < DateTime.Now)
                            {
                                //Si falta algún parametro y lleva mas de un día se actualiza con error
                                notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Error;
                                mEstadoProceso = LogStatus.Error;
                                pLoggingService.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(LogStatus.Error, "No se ha enviado la notificación " + pNotificacion.Clave + " porque lleva 1 hora y faltan parámetros"));
                            }
                        }
                        else
                        {
                            if (pNotificacion.Origen != OrigenesNotificacion.Sugerencia && email != "" && UtilCadenas.ValidarEmail(email))
                            {
                                string emailEnvio = ObtenerParametrosConfiguracionEnvioCorreo(proyectoID, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication).email;
                                string emailSugerencias = ObtenerParametrosConfiguracionEnvioCorreo(proyectoID, pEntityContext, pLoggingService, servicesUtilVirtuosoAndReplication).emailsugerencias;

                                switch (pNotificacion.Origen)
                                {
                                    case OrigenesNotificacion.Sugerencia:
                                        gestorCorreo.EnviarCorreo(emailSugerencias, emailSugerencias, remitente, MiAsunto, cabecera + MiMensaje + pie, true, pNotificacion.Clave);
                                        break;
                                    case OrigenesNotificacion.Suscripcion:
                                        //Reemplazo de la cabecera original los estilos de fondo gris y la imagen por una blanca con el logo
                                        string cabModificada = cabecera.Replace("banner.jpg", "bannerBlanco.jpg").Replace("bgFooter.png", "bgFooterBlanco.png");
                                        gestorCorreo.EnviarCorreo(email, emailEnvio, remitente, MiAsunto, cabModificada + MiMensaje + pie, true, pNotificacion.Clave);
                                        break;
                                    default:
                                        gestorCorreo.EnviarCorreo(email, emailEnvio, remitente, MiAsunto, cabecera + MiMensaje + pie, true, pNotificacion.Clave);
                                        break;
                                }
                            }

                            pLoggingService.GuardarLog(LogStatus.Enviando.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ")  \nNotificacionID : " + pNotificacion.Clave.ToString() + "\nEmail : " + email + "\nFecha : " + DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss") + "\nAsunto : " + MiAsunto + "\nCuerpo : " + MiMensaje);

                            try
                            {
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionCorreoPersona correoPersona in pNotificacion.FilaNotificacion.NotificacionCorreoPersona.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionCorreoPersona.Remove(correoPersona);
                                    pEntityContext.NotificacionCorreoPersona.Remove(correoPersona);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionAlertaPersona notificacionAlertaPersona in pNotificacion.FilaNotificacion.NotificacionAlertaPersona.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionAlertaPersona.Remove(notificacionAlertaPersona);
                                    pEntityContext.NotificacionAlertaPersona.Remove(notificacionAlertaPersona);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionParametro notificacionParametro in pNotificacion.FilaNotificacion.NotificacionParametro.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionParametro.Remove(notificacionParametro);
                                    pEntityContext.NotificacionParametro.Remove(notificacionParametro);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionParametroPersona notificacionParametrosPersona in pNotificacion.FilaNotificacion.NotificacionParametroPersona.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionParametroPersona.Remove(notificacionParametrosPersona);
                                    pEntityContext.NotificacionParametroPersona.Remove(notificacionParametrosPersona);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionSolicitud notificacionSolicitud in pNotificacion.FilaNotificacion.NotificacionSolicitud.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionSolicitud.Remove(notificacionSolicitud);
                                    pEntityContext.NotificacionSolicitud.Remove(notificacionSolicitud);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.NotificacionSuscripcion notificacionSuscrpcion in pNotificacion.FilaNotificacion.NotificacionSuscripcion.ToList())
                                {
                                    pNotificacion.FilaNotificacion.NotificacionSuscripcion.Remove(notificacionSuscrpcion);
                                }
                                foreach (AD.EntityModel.Models.Notificacion.Invitacion invitacion in pNotificacion.FilaNotificacion.Invitacion.ToList())
                                {
                                    pNotificacion.FilaNotificacion.Invitacion.Remove(invitacion);
                                    pEntityContext.Invitacion.Remove(invitacion);
                                }

                                pEntityContext.Notificacion.Remove(pNotificacion.FilaNotificacion);
                                pEntityContext.SaveChanges();
                            }
                            catch (Exception ex)
                            {
                                //Ha dado un error al borrar las filas, actualizo la fila de NotificacionCorreoPersona con la fecha actual y el estado de enviado
                                notificacionCorreo.FechaEnvio = DateTime.Now;
                                notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Enviado;
                            }
                        }


                    }
                    else
                    {
                        //Actualizo la fila de NotificacionCorreoPersona con la fecha actual y el estado de cancelado
                        notificacionCorreo.FechaEnvio = DateTime.Now;
                        notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Cancelado;
                    }
                }
                catch (SmtpException ex)
                {
                    pLoggingService.GuardarLog(ex.Message);
                    //La configuración del buzon de correo (cuenta, usuario, password) es erronea, se escribe en el log y se detiene el servicio.

                    //TODO: notificar al usuario que ha introducido mal el correo de destino

                    if (mErroresNotificables.Contains(ex.StatusCode))
                    {
                        //Actualiza la fila NotificacionCorreoPersona con el estado de cancelado
                        notificacionCorreo.FechaEnvio = DateTime.Now;
                        notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Cancelado;
                        mEstadoProceso = LogStatus.Error;
                    }
                    else
                    {
                        //TODO : Miguel revisar da error si el correo tiene una 'Ñ'
                        notificacionCorreo.FechaEnvio = DateTime.Now;
                        notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Error;
                        mEstadoProceso = LogStatus.Error;
                    }
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    pLoggingService.GuardarLog(ex.Message);
                    notificacionCorreo.FechaEnvio = DateTime.Now;
                    if (notificacionCorreo.EstadoEnvio == (short)EstadoEnvio.Error)
                    {
                        //Actualiza la fila NotificacionCorreoPersona con el estado de cancelado
                        notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Cancelado;
                    }
                    else
                    {
                        //Actualiza la fila NotificacionCorreoPersona con el estado de error
                        notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Error;
                    }
                    mEstadoProceso = LogStatus.Error;
                }
                catch (Exception ex)
                {
                    pLoggingService.GuardarLog(ex.Message);
                    throw;
                    //notificacionCorreo.FechaEnvio = DateTime.Now;
                    //if (notificacionCorreo.EstadoEnvio == (short)EstadoEnvio.Error)
                    //{
                    //    //Actualiza la fila NotificacionCorreoPersona con el estado de cancelado
                    //    notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Cancelado;
                    //}
                    //else
                    //{
                    //    //Actualiza la fila NotificacionCorreoPersona con el estado de error
                    //    notificacionCorreo.EstadoEnvio = (short)EstadoEnvio.Error;
                    //}
                    //mEstadoProceso = LogStatus.Error;

                    ////Recogemos el innerexception
                    //if (ex.InnerException != null)
                    //{
                    //    this.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") InnerExceptionMessage: " + this.CrearEntradaRegistro(LogStatus.Error, ex.InnerException.Message) + " InnerException StackTrace: " + ex.InnerException.StackTrace);
                    //}
                    //else
                    //{
                    //    this.GuardarLog(LogStatus.Error.ToString().ToUpper() + " (" + mFicheroConfiguracionBD + ") " + this.CrearEntradaRegistro(LogStatus.Error, ex.Message));
                    //}
                }
            }

            persCN.Dispose();
            proyCN.Dispose();
        }

        /// <summary>
        /// Comprueba la configuración de notificaciones en función del tipo de mensaje que se pasa por parámetro
        /// </summary>
        /// <param name="pFilaConfig">Fila de configuración de la cuenta</param>
        /// <param name="pMensajeID">Identificador del mensaje</param>
        /// <returns>TRUE si permite el envío del tipo de mensaje dado, FALSE en caso contrario</returns>
        private bool ComprobarPermisos(AD.EntityModel.Models.PersonaDS.ConfiguracionGnossPersona pFilaConfig, short pMensajeID)
        {
            if (pMensajeID >= 52 && pMensajeID <= 57)
            {
                return pFilaConfig.SolicitudesContacto;
            }
            else if (pMensajeID >= 120 && pMensajeID <= 149)
            {
                return pFilaConfig.MensajesGnoss;
            }
            else if (pMensajeID == 161)
            {
                return pFilaConfig.ComentariosRecursos;
            }
            else if (pMensajeID == 49)
            {
                return pFilaConfig.InvitacionComunidad;
            }
            else if (pMensajeID >= 50 && pMensajeID <= 51)
            {
                return pFilaConfig.InvitacionOrganizacion;
            }
            return true;
        }

        private bool AgnadirColetillaNotificacionPerfil(short pMensajeID)
        {
            //Se deba añadir la coletilla en los mensajes que se corresponden con las notificaciones configuradas por el usuario y ademas en las invitaciones, verificación de contraseña y verificaciones/solicitudes 

            if (pMensajeID >= 20 && pMensajeID <= 39)
            {
                //Solicitudes/verificaciones
                return true;
            }
            if (pMensajeID == 80)
            {
                //Solicitud cambio password
                return true;
            }
            else if (pMensajeID >= 40 && pMensajeID <= 59)
            {
                //Invitaciones
                return true;
            }
            #region Notificaciones
            else if (pMensajeID >= 52 && pMensajeID <= 57)
            {
                //Solicitudes contacto
                return true;
            }
            else if (pMensajeID >= 120 && pMensajeID <= 149)
            {
                //Mensajes gnoss
                return true;
            }
            else if (pMensajeID == 161)
            {
                //Comentarios en recursos
                return true;
            }
            else if (pMensajeID == 49)
            {
                //Invitaciones comunidad
                return true;
            }
            else if (pMensajeID >= 50 && pMensajeID <= 51)
            {
                //Invitaciones org
                return true;
            }
            else if (pMensajeID == 60)
            {
                //Boletín suscripción
                return true;
            }
            #endregion
            else
            {
                //Si no es ninguno de lo anteriores
                return false;
            }
        }

        /// <summary>
        /// Determina el texto de la entrada que tendrá una operación de envío de notificaciones
        /// </summary>
        /// <param name="pStatus">Estado de la operación de envio</param>
        /// <param name="pDetalles">Detalles de la operación de envio</param>
        /// <returns>Texto incluyendo estado y detalles del envío</returns>
        private string CrearEntradaRegistro(LogStatus pEstado, string pDetalles)
        {
            string entradaLog = string.Empty;

            switch (pEstado)
            {
                case LogStatus.Correcto:
                    entradaLog = "\r\n\t >> OK: ";
                    break;
                case LogStatus.Error:
                    entradaLog = "\r\n\t >> ALERT: ";
                    break;
                case LogStatus.NoEnviado:
                    entradaLog = "\r\n\t >> OK: ";
                    break;
            }
            return entradaLog + pDetalles;
        }

        /// <summary>
        /// Carga las notificaciones que deben enviarse
        /// </summary>
        private GestionNotificaciones CargarDatos(EntityContext pEntityContext, LoggingService pLoggingService, IServicesUtilVirtuosoAndReplication servicesUtilVirtuosoAndReplication)
        {
            bool cargarFallidas = false;
            long ahora = DateTime.Now.Ticks;
            long dif = ahora - mHoraUltimaCarga;
            if (dif > mIntervalo)
            {
                cargarFallidas = true;
                mHoraUltimaCarga = ahora;
            }

            NotificacionCN notificacionCN = new NotificacionCN(pEntityContext, pLoggingService, mConfigService, servicesUtilVirtuosoAndReplication);
            GestionNotificaciones gestorNotificaciones = new GestionNotificaciones(notificacionCN.ObtenerEnvioNotificaciones(cargarFallidas), pLoggingService, pEntityContext, mConfigService, servicesUtilVirtuosoAndReplication);

            return gestorNotificaciones;
        }

        #endregion

        #endregion

        #region Propiedades

        /// <summary>
        /// Obtiene el proyecto principal de un ecosistema sin metaproyecto
        /// </summary>
        public Guid ProyectoPrincipalUnico
        {
            get
            {
                if (mProyectoPrincipalUnico == null)
                {
                    mProyectoPrincipalUnico = ProyectoAD.MetaProyecto;
                    if (GestorParametroAplicacionDS.ParametroAplicacion.Where(parametroAplicacion => parametroAplicacion.Parametro.Equals("ComunidadPrincipalID")).ToList().Count > 0)
                    {
                        mProyectoPrincipalUnico = new Guid(GestorParametroAplicacionDS.ParametroAplicacion.Where(parametroAplicacion => parametroAplicacion.Parametro.Equals("ComunidadPrincipalID")).FirstOrDefault().Valor);
                    }
                }
                return mProyectoPrincipalUnico.Value;
            }
        }

        //private string UrlIntragnoss(EntityContext pEntityContext, LoggingService pLoggingService)
        //{
        //    get
        //    {
        //        if (mUrlIntragnossBBDD == null)
        //        {
        //            ProyectoCN proyCN = new ProyectoCN();
        //            mUrlIntragnossBBDD = proyCN.ObtenerURLPropiaProyecto(ProyectoAD.MetaProyecto);

        //            if (string.IsNullOrEmpty(mUrlIntragnossBBDD))
        //            {
        //                ParametroAplicacionCN paramApliCN = new ParametroAplicacionCN(mFicheroConfiguracionBD);
        //                mUrlIntragnossBBDD = paramApliCN.ObtenerUrl();
        //                paramApliCN.Dispose();
        //            }

        //            if (!mUrlIntragnossBBDD.EndsWith("/"))
        //            {
        //                mUrlIntragnossBBDD += "/";
        //            }
        //        }

        //        return mUrlIntragnossBBDD;
        //    }
        //}

        public Dictionary<Guid, string> UrlsPropiasProyectos
        {
            get
            {
                if (mUrlsPropiasProyectos == null)
                {
                    mUrlsPropiasProyectos = new Dictionary<Guid, string>();
                }

                return mUrlsPropiasProyectos;
            }
        }

        /// <summary>
        /// Obtiene si se trata de un ecosistema sin metaproyecto
        /// </summary>
        public bool EsEcosistemaSinMetaProyecto
        {
            get
            {
                if (!mEsEcosistemaSinMetaProyecto.HasValue)
                {
                    mEsEcosistemaSinMetaProyecto = GestorParametroAplicacionDS.ParametroAplicacion.Where(parametroAplicacion => parametroAplicacion.Parametro.Equals(TiposParametrosAplicacion.EcosistemaSinMetaProyecto.ToString())).ToList().Count > 0 && bool.Parse((string)GestorParametroAplicacionDS.ParametroAplicacion.Where(parametroAplicacion => parametroAplicacion.Parametro.Equals(TiposParametrosAplicacion.EcosistemaSinMetaProyecto.ToString())).FirstOrDefault().Valor);
                }
                return mEsEcosistemaSinMetaProyecto.Value;
            }
        }
        #endregion
    }
}
