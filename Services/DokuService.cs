using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace olx_be_api.Services
{
    public class DokuService : IDokuService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DokuService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public DokuService(HttpClient httpClient, IConfiguration configuration, ILogger<DokuService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
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

            var payload = new DokuRequestPayload
            {
                Order = new DokuOrderRequest
                {
                    Amount = request.Amount,
                    InvoiceNumber = request.InvoiceNumber,
                    Currency = "IDR",
                    CallbackUrl = callbackUrl,
                    LineItems = request.LineItems
                },
                Payment = new DokuPaymentDetails
                {
                    PaymentDueDate = 60
                },
                Customer = new DokuCustomerDetails
                {
                    Name = request.CustomerName,
                    Email = request.CustomerEmail
                }
            };

            var requestJson = JsonSerializer.Serialize(payload, _jsonOptions);
            _logger.LogInformation("FINAL JSON sebelum dikirim ke DOKU:\n{0}", requestJson);

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

            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                var stringToSignBytes = Encoding.UTF8.GetBytes(stringToSign);
                var signatureBytes = hmac.ComputeHash(stringToSignBytes);
                signature = Convert.ToBase64String(signatureBytes);
            }
            var signatureHeader = $"HMACSHA256={signature}";

            _logger.LogInformation("Digest Header: {0}", digestHeader);
            _logger.LogInformation("Signature Header: {0}", signatureHeader);

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", requestTimestamp);
                requestMessage.Headers.Add("Digest", digestHeader);
                requestMessage.Headers.Add("Signature", signatureHeader);
                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(requestMessage);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gagal membuat pembayaran DOKU. Status: {0}, Body: {1}", response.StatusCode, responseBody);
                    return new DokuPaymentResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Error DOKU: {responseBody}"
                    };
                }

                using var responseDoc = JsonDocument.Parse(responseBody);
                var paymentUrl = responseDoc.RootElement
                    .GetProperty("response")
                    .GetProperty("payment")
                    .GetProperty("url")
                    .GetString();

                if (string.IsNullOrEmpty(paymentUrl))
                {
                    return new DokuPaymentResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = "Gagal mendapatkan URL pembayaran dari respons DOKU."
                    };
                }

                return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terjadi kesalahan saat membuat pembayaran DOKU.");
                return new DokuPaymentResponse
                {
                    IsSuccess = false,
                    ErrorMessage = $"Terjadi kesalahan sistem: {ex.Message}"
                };
            }
        }
    }
}