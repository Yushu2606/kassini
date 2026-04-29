// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Kassini.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Kassini.Authentication;

internal sealed class ValidateTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ValidateTokenMiddleware> _logger;
    private readonly ServerSection _server;

    public ValidateTokenMiddleware(RequestDelegate next, ILogger<ValidateTokenMiddleware> logger, ServerSection server)
    {
        _next = next;
        _logger = logger;
        _server = server;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Query.TryGetValue("t", out var value) &&
            _server?.Authentication?.BrowserToken != null)
        {
            if (await TryAuthenticateAsync(value.ToString(), context, _server.Authentication.BrowserToken).ConfigureAwait(false))
            {
                // Success. Redirect to the app.
                if (context.Request.Query.TryGetValue("returnUrl", out var returnUrl))
                {
                    context.Response.Redirect(GetSafeReturnUrl(returnUrl.ToString()));
                }
                else
                {
                    context.Response.Redirect("/");
                }
            }
            else
            {
                // Failure.
                // The bad token in the query string could be confusing with the token in the text box.
                // Remove it before the presenting the UI to the user.
                var qs = HttpUtility.ParseQueryString(context.Request.QueryString.ToString());
                qs.Remove("t");

                // Collection created by ParseQueryString handles escaping names and values.
                var newQuerystring = qs.ToString();
                if (!string.IsNullOrEmpty(newQuerystring))
                {
                    newQuerystring = "?" + newQuerystring;
                }
                context.Response.Redirect($"{context.Request.Path}{newQuerystring}");
            }

            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    public static async Task<bool> TryAuthenticateAsync(string incomingBrowserToken, HttpContext httpContext, string expectedBrowserTokenBytes)
    {
        if (string.IsNullOrEmpty(incomingBrowserToken))
        {
            return false;
        }

        if (!CompareKey(expectedBrowserTokenBytes, incomingBrowserToken))
        {
            return false;
        }

        var claimsIdentity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "Local")],
            authenticationType: CookieAuthenticationDefaults.AuthenticationScheme);
        var claims = new ClaimsPrincipal(claimsIdentity);

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claims,
            new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);
        return true;
    }

    internal static bool CompareKey(string key1, string key2)
    {
        const int StackAllocThreshold = 256;

        var key1ByteCount = Encoding.UTF8.GetByteCount(key1);
        var key2ByteCount = Encoding.UTF8.GetByteCount(key2);

        // A rented array could have previous data. However, we're trimming it to the exact byte count we need.
        // That means all used bytes are overwritten by the following Encoding.GetBytes call.
        byte[]?
            key1Pooled = null,
            key2Pooled = null;

        var key1BytesSpan = (key1ByteCount <= StackAllocThreshold ?
            stackalloc byte[StackAllocThreshold] :
            (key1Pooled = ArrayPool<byte>.Shared.Rent(key1ByteCount))).Slice(0, key1ByteCount);

        var key2BytesSpan = (key2ByteCount <= StackAllocThreshold ?
            stackalloc byte[StackAllocThreshold] :
            (key2Pooled = ArrayPool<byte>.Shared.Rent(key2ByteCount))).Slice(0, key2ByteCount);

        try
        {
            Encoding.UTF8.GetBytes(key1, key1BytesSpan);
            Encoding.UTF8.GetBytes(key2, key2BytesSpan);

            return CryptographicOperations.FixedTimeEquals(key1BytesSpan, key2BytesSpan);
        }
        finally
        {
            if (key1Pooled != null)
            {
                ArrayPool<byte>.Shared.Return(key1Pooled);
            }

            if (key2Pooled != null)
            {
                ArrayPool<byte>.Shared.Return(key2Pooled);
            }
        }
    }

    internal static string GetSafeReturnUrl(string? returnUrl)
    {
        return IsLocalUrl(returnUrl) ? returnUrl! : "/";
    }

    private static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        return url.Length > 1 && url[0] == '~' && url[1] == '/';
    }
}
