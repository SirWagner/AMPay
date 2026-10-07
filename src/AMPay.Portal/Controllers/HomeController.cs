using Microsoft.AspNetCore.Mvc;

namespace AMPay.Portal.Controllers;

/// <summary>
/// The bare domain. Deliberately lists no lenders: a client arrives through their lender's
/// own link, and a directory would only help someone harvesting codes.
/// </summary>
public class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View();

    [HttpGet("/error")]
    public IActionResult Error() => View();
}
