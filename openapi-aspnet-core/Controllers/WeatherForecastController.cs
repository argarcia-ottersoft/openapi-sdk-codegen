using Microsoft.AspNetCore.Mvc;

namespace openapi_aspnet_core.Controllers
{
    [ApiController]
    [Route("[controller]/[action]")]
    public class WeatherForecastController : ControllerBase
    {
        private static readonly string[] Summaries = new[]
        {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

        private readonly ILogger<WeatherForecastController> _logger;

        public WeatherForecastController(ILogger<WeatherForecastController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public WeatherForecast GetTomorrow()
        {
            return new WeatherForecast
            {
                Date = DateTime.Now.AddDays(1),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            };
        }

        [HttpGet]
        public WeatherForecast GetToday()
        {
            return new WeatherForecast
            {
                Date = DateTime.Now,
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            };
        }

        [HttpGet]
        public IEnumerable<WeatherForecast> GetRange(int start, int count)
        {
            return Enumerable.Range(start, count).Select(index => new WeatherForecast
                {
                    Date = DateTime.Now.AddDays(index),
                    TemperatureC = Random.Shared.Next(-20, 55),
                    Summary = Summaries[Random.Shared.Next(Summaries.Length)]
                })
                .ToArray();
        }

        [HttpGet]
        public WeatherForecast Get(int id)
        {
            return new WeatherForecast
            {
                Date = DateTime.Now.AddDays(id),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            };
        }

        [HttpPost]
        public WeatherForecast Post([FromBody] WeatherForecastPayload payload)
        {
            return new WeatherForecast
            {
                Date = payload.Date,
                TemperatureC = payload.TemperatureC,
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            };
        }

        [HttpPost]
        public WeatherForecast PostById(string id, [FromBody] WeatherForecastPayload payload)
        {
            Console.WriteLine(id);
            return new WeatherForecast
            {
                Date = payload.Date,
                TemperatureC = payload.TemperatureC,
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            };
        }

        [HttpPost]
        public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> files)
        {
            long size = files.Sum(f => f.Length);

            foreach (var formFile in files)
            {
                if (formFile.Length > 0)
                {
                    var filePath = Path.GetTempFileName();

                    using (var stream = System.IO.File.Create(filePath))
                    {
                        await formFile.CopyToAsync(stream);
                    }
                }
            }

            // Process uploaded files
            // Don't rely on or trust the FileName property without validation.

            return Ok(new { count = files.Count, size });
        }
    }

    public record WeatherForecastPayload(DateTime Date, int TemperatureC);
}