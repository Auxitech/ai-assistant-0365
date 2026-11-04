using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MailRouter.Web.Pages;

public class LoginModel(IConfiguration config) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        var expected = config["Admin:Password"];
        if (string.IsNullOrEmpty(expected))
        {
            Error = "Admin password is not configured on the server (ADMIN__PASSWORD).";
            return Page();
        }
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Password), Encoding.UTF8.GetBytes(expected)))
        {
            await Task.Delay(1000);
            Error = "Wrong password.";
            return Page();
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
