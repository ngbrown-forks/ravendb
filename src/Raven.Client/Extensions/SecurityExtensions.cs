using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using Raven.Abstractions.Connection;
using Raven.Client.Connection;
using Raven.Client.OAuth;

namespace Raven.Client.Extensions
{
    internal static class SecurityExtensions
    {
        //TODO: this can be used in document store/counter stores/time series store
        internal static void InitializeSecurity(ConventionBase conventions, HttpJsonRequestFactory requestFactory, string serverUrl, ICredentials primaryCredentials)
        {
            if (conventions.HandleUnauthorizedResponseAsync != null)
                return; // already setup by the user

            var securedAuthenticator = new SecuredAuthenticator();

            requestFactory.ConfigureRequest += securedAuthenticator.ConfigureRequest;

            conventions.HandleForbiddenResponseAsync = (forbiddenResponse, credentials) =>
            {
                if (credentials.ApiKey == null)
                {
                    // AssertForbiddenCredentialSupportWindowsAuth(forbiddenResponse, primaryCredentials);
                    return null;
                }

                return null;
            };

            conventions.HandleUnauthorizedResponseAsync = (unauthorizedResponse, credentials) =>
            {
                var oauthSource = unauthorizedResponse.Headers.GetFirstValue("OAuth-Source");

#if DEBUG && FIDDLER
                // Make sure to avoid a cross DNS security issue, when running with Fiddler
                if (string.IsNullOrEmpty(oauthSource) == false)
                    oauthSource = oauthSource.Replace("localhost:", "localhost.fiddler:");
#endif

                if (credentials.ApiKey == null)
                {
                    // AssertUnauthorizedCredentialSupportWindowsAuth(unauthorizedResponse, credentials.Credentials);
                    return null;
                }

                if (string.IsNullOrEmpty(oauthSource))
                    oauthSource = serverUrl + "/OAuth/API-Key";

                return securedAuthenticator.DoOAuthRequestAsync(oauthSource, credentials.ApiKey);
            };

        }

        private static void AssertForbiddenCredentialSupportWindowsAuth(HttpResponseMessage response, ICredentials credentials)
        {
            if (credentials == null)
                return;

            var requiredAuth = response.Headers.GetFirstValue("Raven-Required-Auth");
            if (requiredAuth == "Windows")
            {
                // we are trying to do windows auth, but we didn't get the windows auth headers
                throw new SecurityException(
                    "Attempted to connect to a RavenDB Server that requires authentication using Windows credentials, but the specified server does not support Windows authentication." +
                    Environment.NewLine +
                    "If you are running inside IIS, make sure to enable Windows authentication.");
            }
        }

        private static void AssertUnauthorizedCredentialSupportWindowsAuth(HttpResponseMessage response, ICredentials credentials)
        {
            if (credentials == null)
                return;

            var authHeaders = response.Headers.WwwAuthenticate.FirstOrDefault();
            if (authHeaders == null || (authHeaders.ToString().Contains("NTLM") == false && authHeaders.ToString().Contains("Negotiate") == false))
            {
                // we are trying to do windows auth, but we didn't get the windows auth headers
                throw new SecurityException(
                    "Attempted to connect to a RavenDB Server that requires authentication using Windows credentials," + Environment.NewLine
                    + " but either wrong credentials were entered or the specified server does not support Windows authentication." +
                    Environment.NewLine +
                    "If you are running inside IIS, make sure to enable Windows authentication.");
            }
        }
    }
}
