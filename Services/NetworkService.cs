// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OptiscalerClient.Models;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Centralized HTTP client factory that respects proxy configuration.
    /// All services should obtain their HttpClient from here so that proxy
    /// settings are applied consistently across the application.
    /// </summary>
    public static class NetworkService
    {
        private static readonly string[] MirrorableGitHubHosts =
        {
            "api.github.com",
            "github.com",
            "codeload.github.com",
            "raw.githubusercontent.com",
            "gist.githubusercontent.com",
        };

        private static readonly object _lock = new();
        private static HttpClient _httpClient = BuildClient(null);

        /// <summary>Returns the shared <see cref="HttpClient"/> configured with the current proxy settings.</summary>
        public static HttpClient GetHttpClient() => _httpClient;

        /// <summary>
        /// Reconfigures the shared <see cref="HttpClient"/> with the provided <paramref name="config"/>.
        /// The old client is disposed after the swap. In-flight requests on the old client may fail.
        /// </summary>
        public static void Configure(NetworkConfig config)
        {
            var newClient = BuildClient(config);
            lock (_lock)
            {
                var old = _httpClient;
                _httpClient = newClient;
                old.Dispose();
            }
        }

        private static HttpClient BuildClient(NetworkConfig? config)
        {
            var transport = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (config != null
                && !config.UseSystemProxy
                && !string.IsNullOrWhiteSpace(config.ProxyHost)
                && config.ProxyPort.HasValue)
            {
                try
                {
                    var scheme = string.Equals(config.ProxyType, "SOCKS5", StringComparison.OrdinalIgnoreCase)
                        ? "socks5"
                        : "http";
                    var proxyUri = new Uri($"{scheme}://{config.ProxyHost}:{config.ProxyPort}");
                    var proxy = new WebProxy(proxyUri);
                    if (config.ProxyRequiresAuth && !string.IsNullOrEmpty(config.ProxyUsername))
                    {
                        proxy.Credentials = new NetworkCredential(
                            config.ProxyUsername,
                            config.ProxyPassword ?? string.Empty);
                    }
                    transport.Proxy = proxy;
                    transport.UseProxy = true;
                }
                catch
                {
                    // Invalid proxy settings — fall back to system proxy silently.
                    transport.UseProxy = true;
                }
            }
            else
            {
                // Honor system proxy or HTTP_PROXY / HTTPS_PROXY env vars.
                transport.UseProxy = true;
            }

            HttpMessageHandler handler = config == null
                ? transport
                : new GitHubMirrorHandler(transport, config);
            var client = new HttpClient(handler, disposeHandler: true);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OptiscalerClient/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        /// <summary>
        /// Normalizes a mirror prefix entered in Settings. A bare host gets https://, a trailing
        /// slash is enforced, and the web-UI form "https://gh-proxy.com/#https://..." is reduced
        /// to its usable proxy prefix.
        /// </summary>
        public static string NormalizeGitHubMirrorPrefix(string? value)
        {
            const string fallback = "https://gh-proxy.com/";
            var prefix = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(prefix)) return fallback;

            var hashIndex = prefix.IndexOf("#http", StringComparison.OrdinalIgnoreCase);
            if (hashIndex > 0) prefix = prefix[..hashIndex];
            prefix = prefix.TrimEnd('#', '/');

            if (!prefix.Contains("://", StringComparison.Ordinal))
                prefix = "https://" + prefix.TrimStart('/');

            if (!Uri.TryCreate(prefix + "/", UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return fallback;

            return uri.AbsoluteUri;
        }

        /// <summary>Returns the mirror URL for a supported GitHub URL, or <paramref name="url"/> unchanged.</summary>
        public static string GetMirroredGitHubUrl(string url, NetworkConfig config)
        {
            if (!config.UseGitHubMirror || string.IsNullOrWhiteSpace(url))
                return url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var original))
                return url;
            if (!MirrorableGitHubHosts.Contains(original.Host, StringComparer.OrdinalIgnoreCase))
                return url;

            var prefix = NormalizeGitHubMirrorPrefix(config.GitHubMirrorPrefix);
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return url;

            return prefix + original.AbsoluteUri;
        }

        private sealed class GitHubMirrorHandler : DelegatingHandler
        {
            private readonly NetworkConfig _config;

            public GitHubMirrorHandler(HttpMessageHandler innerHandler, NetworkConfig config)
                : base(innerHandler)
            {
                _config = config;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri != null)
                {
                    var mirrored = GetMirroredGitHubUrl(request.RequestUri.AbsoluteUri, _config);
                    if (!string.Equals(mirrored, request.RequestUri.AbsoluteUri, StringComparison.Ordinal))
                        request.RequestUri = new Uri(mirrored, UriKind.Absolute);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
