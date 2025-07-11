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

        public PaypalController(IHttpClientFactory clientFactory, IConfiguration config)
        {
            _clientFactory = clientFactory;
            _config = config;
        }

        [HttpPost("CreatePayment")]
        public async Task<IActionResult> CreatePayment([FromBody] PayPalPaymentRequest request)
        {
            var clientId = _config["PayPal:ClientId"];
            var secret = _config["PayPal:Secret"];

            var client = _clientFactory.CreateClient();
            var byteArray = Encoding.UTF8.GetBytes($"{clientId}:{secret}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            // Get Access Token
            var tokenRes = await client.PostAsync("https://api-m.sandbox.paypal.com/v1/oauth2/token",
                new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"));

            var tokenJson = await tokenRes.Content.ReadAsStringAsync();
            var token = JsonDocument.Parse(tokenJson).RootElement.GetProperty("access_token").GetString();

            // Create Payment
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payment = new
            {
                intent = "sale",
                payer = new { payment_method = "paypal" },
                redirect_urls = new
                {
                    return_url = "http://localhost:4200/bienvenido",
                    cancel_url = "https://tusitio.com/cancel"
                },
                transactions = new[]
                {
                new
                {
                    amount = new { total = request.Monto , currency = request.Moneda },
                    description = "Suscripción mensual"
                }
            }
            };

            var content = new StringContent(JsonSerializer.Serialize(payment), Encoding.UTF8, "application/json");

            var result = await client.PostAsync("https://api-m.sandbox.paypal.com/v1/payments/payment", content);
            var responseJson = await result.Content.ReadAsStringAsync();

            if (!result.IsSuccessStatusCode)
                return BadRequest(responseJson);

            var json = JsonDocument.Parse(responseJson);
            var approvalLink = json.RootElement
                .GetProperty("links")
                .EnumerateArray()
                .First(x => x.GetProperty("rel").GetString() == "approval_url")
                .GetProperty("href")
                .GetString();

            return Ok(new { url = approvalLink });
        }
    }
}
