using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace olx_be_api.Services
{
    public class DokuService : IDokuService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DokuService> _logger;

        public DokuService(HttpClient httpClient, IConfiguration configuration, ILogger<DokuService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<DokuPaymentResponse> CreatePayment(DokuPaymentRequest request)
        {
            var dokuConfig = _configuration.GetSection("DokuSettings");
            var clientId = dokuConfig["ClientId"];
            var secretKey = dokuConfig["SecretKey"];
            var apiBaseUrl = dokuConfig["ApiUrl"];
            var callbackUrl = dokuConfig["CallbackUrl"]?.Trim();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(apiBaseUrl) || string.IsNullOrEmpty(callbackUrl))
            {
                _logger.LogError("Konfigurasi DOKU tidak lengkap.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var requestTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var requestPath = "/checkout/v1/payment";
            var fullUrl = apiBaseUrl.TrimEnd('/') + requestPath;

            var lineItemsArray = new JArray(
                request.LineItems.Select(item => new JObject
                {
                    { "name", item.Name },
                    { "price", item.Price },
                    { "quantity", item.Quantity }
                })
            );

            var payload = new JObject
            {
                { "order", new JObject
                    {
                        { "amount", request.Amount },
                        { "invoice_number", request.InvoiceNumber },
                        { "currency", "IDR" },
                        { "callback_url", callbackUrl },
                        { "line_items", lineItemsArray }
                    }
                },
                { "payment", new JObject
                    {
                        { "payment_due_date", 60 }
                    }
                },
                { "customer", new JObject
                    {
                        { "name", request.CustomerName },
                        { "email", request.CustomerEmail }
                    }
                }
            };

            var requestJson = payload.ToString(Formatting.None);

            _logger.LogInformation("Generated JSON with JObject: {RequestJson}", requestJson);

            // Generate digest
            string digestValue;
            using (var sha256 = SHA256.Create())
            {
                var requestBodyBytes = Encoding.UTF8.GetBytes(requestJson);
                var hashBytes = sha256.ComputeHash(requestBodyBytes);
                digestValue = Convert.ToBase64String(hashBytes);
            }
            var digestHeader = $"SHA-256={digestValue}";

            var stringToSign = $"Client-Id:{clientId}\n" +
                               $"Request-Id:{requestId}\n" +
                               $"Request-Timestamp:{requestTimestamp}\n" +
                               $"Request-Target:{requestPath}\n" +
                               $"Digest:{digestHeader}";

            _logger.LogInformation("String-to-Sign: {StringToSign}", stringToSign);

            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                var stringToSignBytes = Encoding.UTF8.GetBytes(stringToSign);
                var signatureBytes = hmac.ComputeHash(stringToSignBytes);
                signature = Convert.ToBase64String(signatureBytes);
            }
            var signatureHeader = $"HMACSHA256={signature}";

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", requestTimestamp);
                requestMessage.Headers.Add("Digest", digestHeader);
                requestMessage.Headers.Add("Signature", signatureHeader);

                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                _logger.LogInformation("DOKU Request Headers - Client-Id: {ClientId}, Request-Id: {RequestId}, Request-Timestamp: {RequestTimestamp}, Digest: {Digest}, Signature: {Signature}",
                    clientId, requestId, requestTimestamp, digestHeader, signatureHeader);
                _logger.LogInformation("DOKU Request Body: {RequestBody}", requestJson);

                var response = await _httpClient.SendAsync(requestMessage);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gagal membuat pembayaran DOKU. Status: {StatusCode}, Body: {ResponseBody}", response.StatusCode, responseBody);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Error DOKU: {responseBody}" };
                }

                var dokuResponse = JsonConvert.DeserializeObject<dynamic>(responseBody);
                string paymentUrl = dokuResponse?.response?.payment?.url!;

                if (string.IsNullOrEmpty(paymentUrl))
                {
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Gagal mendapatkan URL pembayaran dari respons DOKU." };
                }

                return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terjadi kesalahan saat membuat pembayaran DOKU.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Terjadi kesalahan sistem: {ex.Message}" };
            }
        }
    }
}