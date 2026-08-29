using BlogApp.BusinnesLayer.DTOs.PaymentDTOs;
using BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace BlogApp.Api.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(IPaymentService paymentService, ILogger<PaymentsController> logger)
    {
        _paymentService = paymentService;
        _logger = logger;
    }

    [Authorize]
    [HttpPost("nowpayments/create")]
    public async Task<IActionResult> CreateNowPayment([FromBody] CreateNowPaymentRequestDto dto)
    {
        try
        {
            var userId = ClaimHelper.GetUserId(User);
            var result = await _paymentService.CreateNowPaymentAsync(userId, dto.CoinPackageId, dto.CoinAmount, dto.PayCurrency);
            return Ok(result);
        }
        catch (PaymentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (PaymentProviderException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Ödəniş provayderi müvəqqəti əlçatan deyil. Bir az sonra yenidən yoxlayın."
            });
        }
    }

    [Authorize]
    [HttpGet("nowpayments/min-amount")]
    public async Task<IActionResult> GetNowPaymentMinimumAmount([FromQuery] string payCurrency)
    {
        try
        {
            var result = await _paymentService.GetNowPaymentMinimumAmountAsync(payCurrency);
            return Ok(result);
        }
        catch (PaymentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (PaymentProviderException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Minimum məbləğ hazırda oxuna bilmir. Bir az sonra yenidən yoxlayın."
            });
        }
    }

    [Authorize]
    [HttpGet("{id:int}/status")]
    public async Task<IActionResult> Status(int id)
    {
        var userId = ClaimHelper.GetUserId(User);
        var result = await _paymentService.GetStatusAsync(userId, id);
        return Ok(result);
    }

    [Authorize]
    [HttpPost("{id:int}/refresh")]
    public async Task<IActionResult> RefreshStatus(int id, [FromQuery] string? nowPaymentsPaymentId)
    {
        try
        {
            var userId = ClaimHelper.GetUserId(User);
            var result = await _paymentService.RefreshStatusAsync(userId, id, nowPaymentsPaymentId);
            return Ok(result);
        }
        catch (PaymentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (PaymentProviderException ex)
        {
            _logger.LogWarning(ex, "NOWPayments status refresh failed for payment {PaymentId}.", id);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "Payment provider status could not be checked. Please try again later.",
                detail = ex.Message,
                inner = ex.InnerException?.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected payment refresh error for payment {PaymentId}.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Unexpected payment refresh error.",
                detail = ex.Message,
                inner = ex.InnerException?.Message
            });
        }
    }

    [AllowAnonymous]
    [HttpPost("nowpayments/ipn")]
    public async Task<IActionResult> Ipn()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();
        var signature = Request.Headers["x-nowpayments-sig"].ToString();

        if (!_paymentService.VerifyIpn(rawBody, signature))
        {
            _logger.LogWarning("Rejected NOWPayments IPN with invalid signature.");
            return Unauthorized();
        }

        NowPaymentsIpnDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<NowPaymentsIpnDto>(rawBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejected NOWPayments IPN with invalid JSON.");
            return BadRequest("Invalid IPN payload.");
        }

        if (dto == null)
            return BadRequest("Invalid IPN payload.");

        try
        {
            await _paymentService.HandleIpnAsync(dto, rawBody);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "NOWPayments IPN could not be applied.");
            return BadRequest("Invalid IPN payload.");
        }

        return Ok();
    }
}
