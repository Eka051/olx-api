using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using olx_be_api.Data;
using olx_be_api.Helpers;
using olx_be_api.Models;
using olx_be_api.Models.Enums;
using olx_be_api.Services;
using System.Text.Json;

namespace olx_be_api.Controllers
{
    [Route("api/payments")]
    [ApiController]
    public class PaymentController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMidtransService _midtransService;

        public PaymentController(AppDbContext context, IMidtransService midtransService)
        {
            _context = context;
            _midtransService = midtransService;
        }

        [HttpPost("premium-subscriptions/{id}/checkout")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CheckoutPremiumAsync(int id)
        {
            var userId = User.GetUserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return Unauthorized(new ApiErrorResponse { message = "Pengguna tidak ditemukan." });
            }

            var package = await _context.PremiumPackages.FindAsync(id);
            if (package == null || !package.IsActive)
            {
                return NotFound(new ApiErrorResponse { success = false, message = "Paket Premium tidak ditemukan" });
            }

            var transactionDetails = new List<TransactionItemDetail>
            {
                new TransactionItemDetail
                {
                    AdPackageId = 0,  
                    ProductId = 0, 
                    Price = package.Price,
                    Quantity = 1
                }
            };

            var transaction = new Transaction
            {
                UserId = userId,
                InvoiceNumber = $"INV-PREMIUM-{DateTime.UtcNow.Ticks}",
                Amount = package.Price,
                Status = TransactionStatus.Pending,
                Type = TransactionType.PremiumSubscription,
                ReferenceId = package.Id.ToString(),
                Details = JsonSerializer.Serialize(transactionDetails)
            };

            var midtransRequest = new MidtransRequest
            {
                InvoiceNumber = transaction.InvoiceNumber,
                Amount = transaction.Amount,
                CustomerDetails = new CustomerDetails
                {
                    FirstName = user.Name ?? "Pengguna OLX",
                    Email = user.Email!
                },
                ItemDetails = new List<ItemDetails>
                {
                    new ItemDetails
                    {
                        Id = package.Id.ToString(),
                        Name = package.Description ?? $"Premium {package.DurationDays} Hari",
                        Price = package.Price,
                        Quantity = 1
                    }
                }
            };

            var midtransResponse = await _midtransService.CreateSnapTransaction(midtransRequest);
            if (!midtransResponse.IsSuccess)
            {
                return StatusCode(500, new ApiErrorResponse { message = midtransResponse.ErrorMessage! });
            }

            transaction.PaymentUrl = midtransResponse.RedirectUrl;
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            return CreatedAtAction(
                nameof(GetPaymentByInvoice),
                new { invoiceNumber = transaction.InvoiceNumber },
                new ApiResponse<string>
                {
                    success = true,
                    message = "URL pembayaran berhasil dibuat",
                    data = midtransResponse.RedirectUrl
                }
            );
        }

        [HttpPost("cart/checkout")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CheckoutCartAsync()
        {
            var userId = User.GetUserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return Unauthorized(new ApiErrorResponse { message = "Pengguna tidak ditemukan." });
            }

            var cartItems = await _context.CartItems
                .Where(ci => ci.UserId == userId)
                .Include(ci => ci.AdPackage)
                .Include(ci => ci.Product)
                .ToListAsync();

            if (!cartItems.Any())
            {
                return NotFound(new ApiErrorResponse { success = false, message = "Keranjang Anda kosong." });
            }
            int totalAmount = cartItems.Sum(item => item.AdPackage.Price * item.Quantity);

            var itemDetails = cartItems.Select(item => new ItemDetails
            {
                Id = item.AdPackageId.ToString(),
                Name = $"Iklan '{item.AdPackage.Name}' untuk '{item.Product.Title}'",
                Price = item.AdPackage.Price,
                Quantity = item.Quantity
            }).ToList();

            var transactionDetails = cartItems.Select(item => new TransactionItemDetail
            {
                AdPackageId = item.AdPackageId,
                ProductId = item.ProductId,
                Price = item.AdPackage.Price,
                Quantity = item.Quantity
            }).ToList();

            var transaction = new Transaction
            {
                UserId = userId,
                InvoiceNumber = $"INV-CART-{DateTime.UtcNow.Ticks}",
                Amount = totalAmount,
                Status = TransactionStatus.Pending,
                Type = TransactionType.AdPackagePurchase,
                ReferenceId = Guid.NewGuid().ToString(),
                Details = JsonSerializer.Serialize(transactionDetails)
            };

            var midtransRequest = new MidtransRequest
            {
                InvoiceNumber = transaction.InvoiceNumber,
                Amount = totalAmount,
                CustomerDetails = new CustomerDetails
                {
                    FirstName = user.Name ?? "Pengguna OLX",
                    Email = user.Email!
                },
                ItemDetails = itemDetails
            };

            var midtransResponse = await _midtransService.CreateSnapTransaction(midtransRequest);
            if (!midtransResponse.IsSuccess)
            {
                return StatusCode(500, new ApiErrorResponse { message = midtransResponse.ErrorMessage! });
            }

            transaction.PaymentUrl = midtransResponse.RedirectUrl;
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            _context.CartItems.RemoveRange(cartItems);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetPaymentByInvoice), new { invoiceNumber = transaction.InvoiceNumber },
                new ApiResponse<string> { success = true, message = "URL pembayaran berhasil dibuat", data = midtransResponse.RedirectUrl });
        }

        [HttpGet("{invoiceNumber}")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<Transaction>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPaymentByInvoice(string invoiceNumber)
        {
            var userId = User.GetUserId();
            var transaction = await _context.Transactions
                .FirstOrDefaultAsync(t => t.InvoiceNumber == invoiceNumber && t.UserId == userId);

            if (transaction == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    success = false,
                    message = "Transaksi tidak ditemukan"
                });
            }

            return Ok(new ApiResponse<Transaction>
            {
                success = true,
                message = "Transaksi berhasil ditemukan",
                data = transaction
            });
        }

        [HttpGet("midtrans/config")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public IActionResult GetMidtransConfig()
        {
            var config = _midtransService.GetConfig();
            return Ok(new ApiResponse<object>
            {
                success = true,
                message = "Midtrans configuration retrieved",
                data = new
                {
                    clientKey = config.ClientKey,
                    isProduction = config.IsProduction,
                    snapUrl = config.IsProduction
                        ? "https://app.midtrans.com/snap/snap.js"
                        : "https://app.sandbox.midtrans.com/snap/snap.js"
                }
            });
        }
    }
}
