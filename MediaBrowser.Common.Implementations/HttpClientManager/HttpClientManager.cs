﻿using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Common.Implementations.HttpClientManager
{
    /// <summary>
    /// Class HttpClientManager
    /// </summary>
    public class HttpClientManager : IHttpClient
    {
        /// <summary>
        /// When one request to a host times out, we'll ban all other requests for this period of time, to prevent scans from stalling
        /// </summary>
        private int TimeoutSeconds = 30;

        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// The _app paths
        /// </summary>
        private readonly IApplicationPaths _appPaths;

        private readonly IFileSystem _fileSystem;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientManager"/> class.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="getHttpClientHandler">The get HTTP client handler.</param>
        /// <exception cref="System.ArgumentNullException">
        /// appPaths
        /// or
        /// logger
        /// </exception>
        public HttpClientManager(IApplicationPaths appPaths, ILogger logger, IFileSystem fileSystem)
        {
            if (appPaths == null)
            {
                throw new ArgumentNullException("appPaths");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            _logger = logger;
            _fileSystem = fileSystem;
            _appPaths = appPaths;
        }

        /// <summary>
        /// Holds a dictionary of http clients by host.  Use GetHttpClient(host) to retrieve or create a client for web requests.
        /// DON'T dispose it after use.
        /// </summary>
        /// <value>The HTTP clients.</value>
        private readonly ConcurrentDictionary<string, HttpClientInfo> _httpClients = new ConcurrentDictionary<string, HttpClientInfo>();

        /// <summary>
        /// Gets
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="enableHttpCompression">if set to <c>true</c> [enable HTTP compression].</param>
        /// <returns>HttpClient.</returns>
        /// <exception cref="System.ArgumentNullException">host</exception>
        private HttpClientInfo GetHttpClient(string host, bool enableHttpCompression)
        {
            if (string.IsNullOrEmpty(host))
            {
                throw new ArgumentNullException("host");
            }

            HttpClientInfo client;

            var key = host + enableHttpCompression;

            if (!_httpClients.TryGetValue(key, out client))
            {
                client = new HttpClientInfo();

                _httpClients.TryAdd(key, client);
            }

            return client;
        }

        private PropertyInfo _httpBehaviorPropertyInfo;

        private HttpWebRequest GetRequest(HttpRequestOptions options, string method, bool enableHttpCompression)
        {
            var request = HttpWebRequest.CreateHttp(options.Url);

            if (!string.IsNullOrEmpty(options.AcceptHeader))
            {
                request.Accept = options.AcceptHeader;
            }

            request.AutomaticDecompression = enableHttpCompression ? DecompressionMethods.Deflate : DecompressionMethods.None;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.Revalidate);
            request.ConnectionGroupName = GetHostFromUrl(options.Url);
            request.KeepAlive = true;
            request.Method = method;
            request.Pipelined = true;
            request.Timeout = 20000;

            if (!string.IsNullOrEmpty(options.UserAgent))
            {
                request.UserAgent = options.UserAgent;
            }

            // This is a hack to prevent KeepAlive from getting disabled internally by the HttpWebRequest
            // May need to remove this for mono
            var sp = request.ServicePoint;
            if (_httpBehaviorPropertyInfo == null)
            {
                _httpBehaviorPropertyInfo = sp.GetType().GetProperty("HttpBehaviour", BindingFlags.Instance | BindingFlags.NonPublic);
            }
            _httpBehaviorPropertyInfo.SetValue(sp, (byte)0, null);

            return request;
        }

        /// <summary>
        /// Gets the response internal.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="httpMethod">The HTTP method.</param>
        /// <returns>Task{HttpResponseInfo}.</returns>
        /// <exception cref="HttpException">
        /// </exception>
        public async Task<HttpResponseInfo> GetResponse(HttpRequestOptions options)
        {
            ValidateParams(options.Url, options.CancellationToken);

            options.CancellationToken.ThrowIfCancellationRequested();

            var client = GetHttpClient(GetHostFromUrl(options.Url), options.EnableHttpCompression);

            if ((DateTime.UtcNow - client.LastTimeout).TotalSeconds < TimeoutSeconds)
            {
                throw new HttpException(string.Format("Cancelling connection to {0} due to a previous timeout.", options.Url)) { IsTimedOut = true };
            }

            var httpWebRequest = GetRequest(options, "GET", options.EnableHttpCompression);

            if (options.ResourcePool != null)
            {
                await options.ResourcePool.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            }

            if ((DateTime.UtcNow - client.LastTimeout).TotalSeconds < TimeoutSeconds)
            {
                if (options.ResourcePool != null)
                {
                    options.ResourcePool.Release();
                }

                throw new HttpException(string.Format("Connection to {0} timed out", options.Url)) { IsTimedOut = true };
            }

            _logger.Info("HttpClientManager.GET url: {0}", options.Url);

            try
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                using (var response = await httpWebRequest.GetResponseAsync().ConfigureAwait(false))
                {
                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(httpResponse);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    using (var stream = httpResponse.GetResponseStream())
                    {
                        var memoryStream = new MemoryStream();

                        await stream.CopyToAsync(memoryStream).ConfigureAwait(false);

                        memoryStream.Position = 0;

                        return new HttpResponseInfo
                        {
                            Content = memoryStream,

                            StatusCode = httpResponse.StatusCode,

                            ContentType = httpResponse.ContentType
                        };
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                var exception = GetCancellationException(options.Url, options.CancellationToken, ex);

                var httpException = exception as HttpException;

                if (httpException != null && httpException.IsTimedOut)
                {
                    client.LastTimeout = DateTime.UtcNow;
                }

                throw exception;
            }
            catch (HttpRequestException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (WebException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw;
            }
            finally
            {
                if (options.ResourcePool != null)
                {
                    options.ResourcePool.Release();
                }
            }
        }

        /// <summary>
        /// Performs a GET request and returns the resulting stream
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Task{Stream}.</returns>
        /// <exception cref="HttpException"></exception>
        /// <exception cref="MediaBrowser.Model.Net.HttpException"></exception>
        public async Task<Stream> Get(HttpRequestOptions options)
        {
            var response = await GetResponse(options).ConfigureAwait(false);

            return response.Content;
        }

        /// <summary>
        /// Performs a GET request and returns the resulting stream
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="resourcePool">The resource pool.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{Stream}.</returns>
        public Task<Stream> Get(string url, SemaphoreSlim resourcePool, CancellationToken cancellationToken)
        {
            return Get(new HttpRequestOptions
            {
                Url = url,
                ResourcePool = resourcePool,
                CancellationToken = cancellationToken,
            });
        }

        /// <summary>
        /// Gets the specified URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{Stream}.</returns>
        public Task<Stream> Get(string url, CancellationToken cancellationToken)
        {
            return Get(url, null, cancellationToken);
        }

        /// <summary>
        /// Performs a POST request
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="postData">Params to add to the POST data.</param>
        /// <returns>stream on success, null on failure</returns>
        /// <exception cref="HttpException">
        /// </exception>
        /// <exception cref="System.ArgumentNullException">postData</exception>
        /// <exception cref="MediaBrowser.Model.Net.HttpException"></exception>
        public async Task<Stream> Post(HttpRequestOptions options, Dictionary<string, string> postData)
        {
            ValidateParams(options.Url, options.CancellationToken);

            options.CancellationToken.ThrowIfCancellationRequested();

            var httpWebRequest = GetRequest(options, "POST", options.EnableHttpCompression);

            var strings = postData.Keys.Select(key => string.Format("{0}={1}", key, postData[key]));
            var postContent = string.Join("&", strings.ToArray());
            var bytes = Encoding.UTF8.GetBytes(postContent);

            httpWebRequest.ContentType = "application/x-www-form-urlencoded";
            httpWebRequest.ContentLength = bytes.Length;
            httpWebRequest.GetRequestStream().Write(bytes, 0, bytes.Length);

            if (options.ResourcePool != null)
            {
                await options.ResourcePool.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            }

            _logger.Info("HttpClientManager.POST url: {0}", options.Url);

            try
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                using (var response = await httpWebRequest.GetResponseAsync().ConfigureAwait(false))
                {
                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(httpResponse);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    using (var stream = httpResponse.GetResponseStream())
                    {
                        var memoryStream = new MemoryStream();

                        await stream.CopyToAsync(memoryStream).ConfigureAwait(false);

                        memoryStream.Position = 0;

                        return memoryStream;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                var exception = GetCancellationException(options.Url, options.CancellationToken, ex);

                throw exception;
            }
            catch (HttpRequestException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (WebException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw;
            }
            finally
            {
                if (options.ResourcePool != null)
                {
                    options.ResourcePool.Release();
                }
            }
        }

        /// <summary>
        /// Performs a POST request
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="postData">Params to add to the POST data.</param>
        /// <param name="resourcePool">The resource pool.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>stream on success, null on failure</returns>
        public Task<Stream> Post(string url, Dictionary<string, string> postData, SemaphoreSlim resourcePool, CancellationToken cancellationToken)
        {
            return Post(new HttpRequestOptions
            {
                Url = url,
                ResourcePool = resourcePool,
                CancellationToken = cancellationToken

            }, postData);
        }

        /// <summary>
        /// Downloads the contents of a given url into a temporary location
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>Task{System.String}.</returns>
        /// <exception cref="System.ArgumentNullException">progress</exception>
        /// <exception cref="HttpException"></exception>
        /// <exception cref="MediaBrowser.Model.Net.HttpException"></exception>
        public async Task<string> GetTempFile(HttpRequestOptions options)
        {
            var response = await GetTempFileResponse(options).ConfigureAwait(false);

            return response.TempFilePath;
        }

        public async Task<HttpResponseInfo> GetTempFileResponse(HttpRequestOptions options)
        {
            ValidateParams(options.Url, options.CancellationToken);

            var tempFile = Path.Combine(_appPaths.TempDirectory, Guid.NewGuid() + ".tmp");

            if (options.Progress == null)
            {
                throw new ArgumentNullException("progress");
            }

            options.CancellationToken.ThrowIfCancellationRequested();

            var httpWebRequest = GetRequest(options, "GET", options.EnableHttpCompression);

            if (options.ResourcePool != null)
            {
                await options.ResourcePool.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            }

            options.Progress.Report(0);

            _logger.Info("HttpClientManager.GetTempFileResponse url: {0}", options.Url);

            try
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                using (var response = await httpWebRequest.GetResponseAsync().ConfigureAwait(false))
                {
                    var httpResponse = (HttpWebResponse)response;

                    EnsureSuccessStatusCode(httpResponse);

                    options.CancellationToken.ThrowIfCancellationRequested();

                    var contentLength = GetContentLength(httpResponse);

                    if (!contentLength.HasValue)
                    {
                        // We're not able to track progress
                        using (var stream = httpResponse.GetResponseStream())
                        {
                            using (var fs = _fileSystem.GetFileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.Read, true))
                            {
                                await stream.CopyToAsync(fs, StreamDefaults.DefaultCopyToBufferSize, options.CancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        using (var stream = ProgressStream.CreateReadProgressStream(httpResponse.GetResponseStream(), options.Progress.Report, contentLength.Value))
                        {
                            using (var fs = _fileSystem.GetFileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.Read, true))
                            {
                                await stream.CopyToAsync(fs, StreamDefaults.DefaultCopyToBufferSize, options.CancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    options.Progress.Report(100);

                    return new HttpResponseInfo
                    {
                        TempFilePath = tempFile,

                        StatusCode = httpResponse.StatusCode,

                        ContentType = httpResponse.ContentType
                    };
                }
            }
            catch (OperationCanceledException ex)
            {
                var exception = GetCancellationException(options.Url, options.CancellationToken, ex);

                throw exception;
            }
            catch (HttpRequestException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (WebException ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw new HttpException(ex.Message, ex);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting response from " + options.Url, ex);

                throw;
            }
            finally
            {
                if (options.ResourcePool != null)
                {
                    options.ResourcePool.Release();
                }
            }
        }

        /// <summary>
        /// Gets the message.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns>HttpResponseMessage.</returns>
        private HttpRequestMessage GetHttpRequestMessage(HttpRequestOptions options)
        {
            var message = new HttpRequestMessage(HttpMethod.Get, options.Url);

            foreach (var pair in options.RequestHeaders.ToList())
            {
                if (!message.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    _logger.Error("Unable to add request header {0} with value {1}", pair.Key, pair.Value);
                }
            }

            return message;
        }

        /// <summary>
        /// Gets the length of the content.
        /// </summary>
        /// <param name="response">The response.</param>
        /// <returns>System.Nullable{System.Int64}.</returns>
        private long? GetContentLength(HttpResponseMessage response)
        {
            IEnumerable<string> lengthValues = null;

            // Seeing some InvalidOperationException here under mono
            try
            {
                response.Headers.TryGetValues("content-length", out lengthValues);
            }
            catch (InvalidOperationException ex)
            {
                _logger.ErrorException("Error accessing response.Headers.TryGetValues Content-Length", ex);
            }

            if (lengthValues == null)
            {
                try
                {
                    response.Content.Headers.TryGetValues("content-length", out lengthValues);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.ErrorException("Error accessing response.Content.Headers.TryGetValues Content-Length", ex);
                }
            }

            if (lengthValues == null)
            {
                return null;
            }

            return long.Parse(string.Join(string.Empty, lengthValues.ToArray()), UsCulture);
        }

        private long? GetContentLength(HttpWebResponse response)
        {
            var length = response.ContentLength;

            if (length == 0)
            {
                return null;
            }

            return length;
        }

        protected static readonly CultureInfo UsCulture = new CultureInfo("en-US");

        /// <summary>
        /// Handles the temp file exception.
        /// </summary>
        /// <param name="ex">The ex.</param>
        /// <param name="options">The options.</param>
        /// <param name="tempFile">The temp file.</param>
        /// <returns>Task.</returns>
        /// <exception cref="HttpException"></exception>
        private Exception GetTempFileException(Exception ex, HttpRequestOptions options, string tempFile)
        {
            var operationCanceledException = ex as OperationCanceledException;

            if (operationCanceledException != null)
            {
                // Cleanup
                DeleteTempFile(tempFile);

                return GetCancellationException(options.Url, options.CancellationToken, operationCanceledException);
            }

            _logger.ErrorException("Error getting response from " + options.Url, ex);

            var httpRequestException = ex as HttpRequestException;

            // Cleanup
            DeleteTempFile(tempFile);

            if (httpRequestException != null)
            {
                return new HttpException(ex.Message, ex);
            }

            return ex;
        }

        private void DeleteTempFile(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Might not have been created at all. No need to worry.
            }
        }

        /// <summary>
        /// Validates the params.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <exception cref="System.ArgumentNullException">url</exception>
        private void ValidateParams(string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url))
            {
                throw new ArgumentNullException("url");
            }
        }

        /// <summary>
        /// Gets the host from URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <returns>System.String.</returns>
        private string GetHostFromUrl(string url)
        {
            var start = url.IndexOf("://", StringComparison.OrdinalIgnoreCase) + 3;
            var len = url.IndexOf('/', start) - start;
            return url.Substring(start, len);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                _httpClients.Clear();
            }
        }

        /// <summary>
        /// Throws the cancellation exception.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>Exception.</returns>
        private Exception GetCancellationException(string url, CancellationToken cancellationToken, OperationCanceledException exception)
        {
            // If the HttpClient's timeout is reached, it will cancel the Task internally
            if (!cancellationToken.IsCancellationRequested)
            {
                var msg = string.Format("Connection to {0} timed out", url);

                _logger.Error(msg);

                // Throw an HttpException so that the caller doesn't think it was cancelled by user code
                return new HttpException(msg, exception) { IsTimedOut = true };
            }

            return exception;
        }

        /// <summary>
        /// Ensures the success status code.
        /// </summary>
        /// <param name="response">The response.</param>
        /// <exception cref="MediaBrowser.Model.Net.HttpException"></exception>
        private void EnsureSuccessStatusCode(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpException(response.ReasonPhrase) { StatusCode = response.StatusCode };
            }
        }

        private void EnsureSuccessStatusCode(HttpWebResponse response)
        {
            var statusCode = response.StatusCode;
            var isSuccessful = statusCode >= HttpStatusCode.OK && statusCode <= (HttpStatusCode)299;

            if (!isSuccessful)
            {
                throw new HttpException(response.StatusDescription) { StatusCode = response.StatusCode };
            }
        }

        /// <summary>
        /// Posts the specified URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="postData">The post data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{Stream}.</returns>
        public Task<Stream> Post(string url, Dictionary<string, string> postData, CancellationToken cancellationToken)
        {
            return Post(url, postData, null, cancellationToken);
        }
    }
}
