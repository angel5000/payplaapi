using Microsoft.AspNetCore.Mvc;
using Paypal.Request;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Paypal.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PaypalController : ControllerBase
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<PaypalController> _logger;

        public PaypalController(IHttpClientFactory clientFactory, IConfiguration config, ILogger<PaypalController> logger)
        {
            _clientFactory = clientFactory;
            _config = config;
            _logger = logger;
        }

        [HttpPost("CreatePayment")]
        public async Task<IActionResult> CreatePayment([FromBody] PayPalPaymentRequest request)
        {
            try
            {
                // Obtener credenciales desde variables de entorno
                var clientId = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID") ?? _config["PayPal:ClientId"];
                var secret = Environment.GetEnvironmentVariable("PAYPAL_SECRET") ?? _config["PayPal:Secret"];
                var baseUrl = Environment.GetEnvironmentVariable("PAYPAL_BASE_URL") ?? "https://api-m.sandbox.paypal.com";

                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
                {
                    _logger.LogError("Credenciales de PayPal no configuradas");
                    return BadRequest(new { error = "Credenciales de PayPal no configuradas" });
                }

                _logger.LogInformation("Iniciando creación de pago para monto: {Monto}", request.Monto);

                var client = _clientFactory.CreateClient();
                var byteArray = Encoding.UTF8.GetBytes($"{clientId}:{secret}");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                // Obtener Access Token
                var tokenRes = await client.PostAsync($"{baseUrl}/v1/oauth2/token",
                    new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"));

                var tokenJson = await tokenRes.Content.ReadAsStringAsync();
                _logger.LogInformation("Respuesta del token: {TokenResponse}", tokenJson);

                if (!tokenRes.IsSuccessStatusCode)
                {
                    _logger.LogError("Error al obtener token de PayPal: {Error}", tokenJson);
                    return BadRequest(new { error = "Error al autenticar con PayPal", details = tokenJson });
                }

                var tokenDocument = JsonDocument.Parse(tokenJson);

                if (!tokenDocument.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                {
                    _logger.LogError("No se encontró access_token en la respuesta: {Response}", tokenJson);
                    return BadRequest(new { error = "No se pudo obtener el token de acceso", details = tokenJson });
                }

                var token = accessTokenElement.GetString();

                // Crear Payment usando la API v2 (recomendada)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("Prefer", "return=representation");

                var payment = new
                {
                    intent = "CAPTURE",
                    purchase_units = new[]
                    {
                        new
                        {
                            amount = new
                            {
                                currency_code = request.Moneda,
                                value = request.Monto
                            },
                            description = "Suscripción mensual"
                        }
                    },
                    application_context = new
                    {
                        return_url = "http://localhost:4200/bienvenido",
                        cancel_url = "http://localhost:4200/cancel",
                        brand_name = "Tu Aplicación",
                        landing_page = "LOGIN",
                        shipping_preference = "NO_SHIPPING",
                        user_action = "PAY_NOW"
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(payment), Encoding.UTF8, "application/json");

                // Usar API v2 en lugar de v1
                var result = await client.PostAsync($"{baseUrl}/v2/checkout/orders", content);
                var responseJson = await result.Content.ReadAsStringAsync();

                _logger.LogInformation("Respuesta de creación de pago: {Response}", responseJson);

                if (!result.IsSuccessStatusCode)
                {
                    _logger.LogError("Error al crear pago: {Error}", responseJson);
                    return BadRequest(new { error = "Error al crear el pago", details = responseJson });
                }

                var json = JsonDocument.Parse(responseJson);

                // Buscar el link de aprobación
                if (!json.RootElement.TryGetProperty("links", out var linksElement))
                {
                    _logger.LogError("No se encontraron links en la respuesta: {Response}", responseJson);
                    return BadRequest(new { error = "No se encontraron links de pago", details = responseJson });
                }

                string approvalLink = null;
                foreach (var link in linksElement.EnumerateArray())
                {
                    if (link.TryGetProperty("rel", out var relElement) &&
                        relElement.GetString() == "approve" &&
                        link.TryGetProperty("href", out var hrefElement))
                    {
                        approvalLink = hrefElement.GetString();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(approvalLink))
                {
                    _logger.LogError("No se encontró link de aprobación: {Response}", responseJson);
                    return BadRequest(new { error = "No se encontró link de aprobación", details = responseJson });
                }

                _logger.LogInformation("Pago creado exitosamente, URL de aprobación: {Url}", approvalLink);

                return Ok(new { url = approvalLink });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado al crear pago");
                return StatusCode(500, new { error = "Error interno del servidor", details = ex.Message });
            }
        }
    }
}