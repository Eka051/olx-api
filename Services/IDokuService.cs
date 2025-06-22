namespace olx_be_api.Services
{
    public class DokuLineItem
    {
        public string? Name { get; set; }
        public int Quantity { get; set; }
        public int Price { get; set; }
    }
    public class DokuPaymentRequest
    {
        public string? InvoiceNumber { get; set; }
        public int Amount { get; set; }
        public string? ProductName { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public List<DokuLineItem> LineItems { get; set; } = new List<DokuLineItem>();
    }

    public class DokuPaymentResponse
    {
        public bool IsSuccess { get; set; }
        public string? PaymentUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public interface IDokuService
    {
        Task<DokuPaymentResponse> CreatePayment(DokuPaymentRequest request);
    }
}
