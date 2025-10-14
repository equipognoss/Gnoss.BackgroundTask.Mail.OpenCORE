using Es.Riam.Gnoss.Elementos.Suscripcion;
using Es.Riam.Gnoss.Servicios;
using Es.Riam.Gnoss.Util.Configuracion;
using Es.Riam.Gnoss.Win.ServicioCorreo;
using Es.Riam.Gnoss.Win.ServicioCorreo.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServicioCorreo
{
    public class CorreoWorker : Worker
    {
        private readonly ConfigService _configService;
        private ILogger mlogger;
        private ILoggerFactory mLoggerFactory;
        public CorreoWorker(ConfigService configService, IServiceScopeFactory scopeFactory, ILogger<CorreoWorker> logger, ILoggerFactory loggerFactory) : base(logger, scopeFactory)
        {
            _configService = configService;
            mlogger = logger;
            mLoggerFactory = loggerFactory;
        }

        protected override List<ControladorServicioGnoss> ObtenerControladores()
        {
            ControladorServicioGnoss.INTERVALO_SEGUNDOS = _configService.ObtenerIntervalo();

            List<ControladorServicioGnoss> controladores = new List<ControladorServicioGnoss>();
            controladores.Add(new CorreoController(ScopedFactory, _configService, mLoggerFactory.CreateLogger<CorreoController>(), mLoggerFactory, 1));
            controladores.Add(new NotificacionController(ScopedFactory, _configService, mLoggerFactory.CreateLogger<NotificacionController>(), mLoggerFactory, 2));

            return controladores;
        }
    }
}
