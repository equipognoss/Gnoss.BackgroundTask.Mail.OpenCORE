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
        private readonly ILogger<CorreoWorker> _logger;
        private readonly ConfigService _configService;

        public CorreoWorker(ILogger<CorreoWorker> logger, ConfigService configService, IServiceScopeFactory scopeFactory) : base(logger, scopeFactory)
        {
            _logger = logger;
            _configService = configService;
        }

        protected override List<ControladorServicioGnoss> ObtenerControladores()
        {
            ControladorServicioGnoss.INTERVALO_SEGUNDOS = _configService.ObtenerIntervalo();

            List<ControladorServicioGnoss> controladores = new List<ControladorServicioGnoss>();
            controladores.Add(new CorreoController(ScopedFactory, _configService, 1));
            controladores.Add(new NotificacionController(ScopedFactory, _configService, 2));

            return controladores;
        }
    }
}
