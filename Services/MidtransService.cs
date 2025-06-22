using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using olx_be_api.Models;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace olx_be_api.Services
{
    public class MidtransService : IMidtransService
    {
        private readonly HttpClient _httpClient;
        private readonly string _serverKey;
        private readonly string _clientKey;
        private readonly string _snapEndpoint;
        private readonly bool _isProduction;

        public MidtransService(HttpClient httpClient, IConfiguration configuration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _serverKey = configuration["Midtrans:ServerKey"] ?? throw new ArgumentNullException("Midtrans:ServerKey");
            _clientKey = configuration["Midtrans:ClientKey"] ?? throw new ArgumentNullException("Midtrans:ClientKey");
            _isProduction = bool.Parse(configuration["Midtrans:IsProduction"] ?? "false");
            _snapEndpoint = _isProduction
                ? "https://app.midtrans.com/snap/v1/transactions"
                : "https://app.sandbox.midtrans.com/snap/v1/transactions";

            var authHeaderValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_serverKey}:"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeaderValue);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<MidtransResponse> CreateSnapTransaction(MidtransRequest request)
        {
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_snapEndpoint, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new MidtransResponse
                {
                    IsSuccess = false,
                    ErrorMessage = responseBody
                };
            }

            var snap = JsonSerializer.Deserialize<SnapResponse>(responseBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new MidtransResponse
            {
                IsSuccess = true,
                SnapToken = snap!.Token,
                RedirectUrl = snap!.RedirectUrl
            };
        }

        public MidtransConfig GetConfig()
        {
            return new MidtransConfig
            {
                ServerKey = _serverKey,
                ClientKey = _clientKey,
                IsProduction = _isProduction
            };
        }
    }
}