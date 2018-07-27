﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GaboG.ServiceBusRelayUtilNetCore.Extensions;
using Microsoft.Azure.Relay;
using Newtonsoft.Json;

namespace GaboG.ServiceBusRelayUtilNetCore

{
    class DispatcherService
    {
        private readonly string _connectionName;
        readonly HttpClient _httpClient;
        readonly string _hybridConnectionSubpath;
        private readonly string _key;
        private readonly string _keyName;
        readonly HybridConnectionListener _listener;
        private readonly string _relayNamespace;
        private readonly Uri _targetServiceAddress;

        public DispatcherService(string relayNamespace, string connectionName, string keyName, string key, Uri targetServiceAddress)
        {
            _relayNamespace = relayNamespace;
            _connectionName = connectionName;
            _keyName = keyName;
            _key = key;
            _targetServiceAddress = targetServiceAddress;

            var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider(_keyName, _key);
            _listener = new HybridConnectionListener(new Uri(string.Format("sb://{0}/{1}", _relayNamespace, _connectionName)), tokenProvider);

            _httpClient = new HttpClient
            {
                BaseAddress = targetServiceAddress
            };
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;

            _hybridConnectionSubpath = _listener.Address.AbsolutePath.EnsureEndsWith("/");
        }

        public async Task OpenAsync(CancellationToken cancelToken)
        {
            _listener.RequestHandler = ListenerRequestHandler;
            await _listener.OpenAsync(cancelToken);
            Console.WriteLine("Azure Service Bus is listening on \n\r\t{0}\n\rand routing requests to \n\r\t{1}\n\r\n\r", _listener.Address, _httpClient.BaseAddress);
            Console.WriteLine("Press [Enter] to exit");
        }

        public Task CloseAsync(CancellationToken cancelToken)
        {
            _httpClient.Dispose();
            return _listener.CloseAsync(cancelToken);
        }

        async void ListenerRequestHandler(RelayedHttpListenerContext context)
        {
            var startTimeUtc = DateTime.UtcNow;
            Console.WriteLine("In ListenerRequestHandler:");
            try
            {
                Console.WriteLine("...calling {0}...", _targetServiceAddress);
                var requestMessage = CreateHttpRequestMessage(context);
                var responseMessage = await _httpClient.SendAsync(requestMessage);
                await SendResponseAsync(context, responseMessage);
                await context.Response.CloseAsync();
            }

            catch (Exception ex)
            {
                LogException(ex);
                SendErrorResponse(ex, context);
            }
            finally
            {
                LogRequest(startTimeUtc, context);
            }
        }

        async Task SendResponseAsync(RelayedHttpListenerContext context, HttpResponseMessage responseMessage)
        {
            context.Response.StatusCode = responseMessage.StatusCode;
            context.Response.StatusDescription = responseMessage.ReasonPhrase;
            foreach (var header in responseMessage.Headers)
            {
                if (string.Equals(header.Key, "Transfer-Encoding"))
                {
                    continue;
                }

                context.Response.Headers.Add(header.Key, string.Join(",", header.Value));
            }

            var responseStream = await responseMessage.Content.ReadAsStreamAsync();
            await responseStream.CopyToAsync(context.Response.OutputStream);
        }

        void SendErrorResponse(Exception e, RelayedHttpListenerContext context)
        {
            context.Response.StatusCode = HttpStatusCode.InternalServerError;

#if DEBUG || INCLUDE_ERROR_DETAILS
            context.Response.StatusDescription = $"Internal Server Error: {e.GetType().FullName}: {e.Message}";
#endif
            context.Response.Close();
        }

        HttpRequestMessage CreateHttpRequestMessage(RelayedHttpListenerContext context)
        {
            var requestMessage = new HttpRequestMessage();
            if (context.Request.HasEntityBody)
            {
                requestMessage.Content = new StreamContent(context.Request.InputStream);
                var contentType = context.Request.Headers[HttpRequestHeader.ContentType];
                if (!string.IsNullOrEmpty(contentType))
                {
                    requestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
            }

            var relativePath = context.Request.Url.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
            relativePath = relativePath.Replace(_hybridConnectionSubpath, string.Empty, StringComparison.OrdinalIgnoreCase);
            requestMessage.RequestUri = new Uri(relativePath, UriKind.RelativeOrAbsolute);
            requestMessage.Method = new HttpMethod(context.Request.HttpMethod);

            foreach (var headerName in context.Request.Headers.AllKeys)
            {
                if (string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    // Don't flow these headers here
                    continue;
                }

                requestMessage.Headers.Add(headerName, context.Request.Headers[headerName]);
            }

            LogRequestActivity(requestMessage);

            return requestMessage;
        }

        void LogRequest(DateTime startTimeUtc, RelayedHttpListenerContext context)
        {
            var stopTimeUtc = DateTime.UtcNow;
            //var buffer = new StringBuilder();
            //buffer.Append($"{startTimeUtc.ToString("s", CultureInfo.InvariantCulture)}, ");
            //buffer.Append($"\"{context.Request.HttpMethod} {context.Request.Url.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped)}\", ");
            //buffer.Append($"{(int)context.Response.StatusCode}, ");
            //buffer.Append($"{(int)stopTimeUtc.Subtract(startTimeUtc).TotalMilliseconds}");
            //Console.WriteLine(buffer);

            Console.WriteLine("...and back {0:N0} ms...", stopTimeUtc.Subtract(startTimeUtc).TotalMilliseconds);
            Console.WriteLine("");
        }

        private void LogRequestActivity(HttpRequestMessage requestMessage)
        {
            var content = requestMessage.Content.ReadAsStringAsync().Result;
            Console.ForegroundColor = ConsoleColor.Yellow;
            var s = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented
            };

            dynamic o = JsonConvert.DeserializeObject(content);
            var formatted = JsonConvert.SerializeObject(o, s);
            Console.WriteLine(formatted);
            Console.ResetColor();
        }

        private static void LogException(Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex);
            Console.WriteLine("");
            Console.ResetColor();
        }
    }
}