/*
Copyright 2010 Google Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using Google.Apis.Authentication;
using Google.Apis.Discovery;
using Google.Apis.Testing;
using Google.Apis.Util;

namespace Google.Apis.Requests
{
    /// <summary>
    /// Request to a service.
    /// </summary>
    public class Request : IRequest
    {
        private const string UserAgent = "{0} google-api-dotnet-client/{1} {2}/{3}";
        private const string GZipUserAgentSuffix = " (gzip)";
        private const string GZipEncoding = "gzip";

        /// <summary>
        /// The charset used for content encoding.
        /// </summary>
        public static readonly Encoding ContentCharset = Encoding.UTF8;

        public const string DELETE = "DELETE";
        public const string GET = "GET";
        public const string PATCH = "PATCH";
        public const string POST = "POST";
        public const string PUT = "PUT";

        /// <summary>
        /// Defines the maximum number of retries which should be made if a request fails.
        /// </summary>
        public int MaximumRetries { get; set; }

        /// <summary>
        /// Defines the factor by which the waiting time is multiplied between retry attempts.
        /// </summary>
        public double RetryWaitTimeIncreaseFactor { get; set; }

        /// <summary>
        /// Defines the initial waiting time, which is used once the first retry has failed.
        /// </summary>
        public int RetryInitialWaitTime { get; set; }

        private static readonly String ApiVersion = typeof(Request).Assembly.GetName().Version.ToString();

        public static readonly ReadOnlyCollection<string> SupportedHttpMethods =
            new List<string> { POST, PUT, DELETE, GET, PATCH }.AsReadOnly();

        private string applicationName;
        private Uri requestUrl;

        public Request()
        {
            applicationName = Utilities.GetAssemblyTitle() ?? "Unknown_Application";
            Authenticator = new NullAuthenticator();
            
            MaximumRetries = 3;
            RetryWaitTimeIncreaseFactor = 2.0;
            RetryInitialWaitTime = 1000;
        }

        /// <summary>
        /// The authenticator used for this request.
        /// </summary>
        internal IAuthenticator Authenticator { get; private set; }

        /// <summary>
        /// The developer API Key sent along with the request.
        /// </summary>
        internal String DeveloperKey { get; private set; }

        /// <summary>
        /// Set of method parameters.
        /// </summary>
        [VisibleForTestOnly]
        internal ParameterCollection Parameters { get; set; }

        /// <summary>
        /// The application name used within the user agent string.
        /// </summary>
        [VisibleForTestOnly]
        internal string ApplicationName
        {
            get { return applicationName; }
        }

        private IService Service { get; set; }
        private IMethod Method { get; set; }
        private Uri BaseURI { get; set; }
        private string RPCName { get; set; } // note: this property is apparently never used
        private string Body { get; set; }
        private ReturnType ReturnType { get; set; }
        private ETagAction ETagAction { get; set; }
        private string ETag { get; set; }
        private string FieldsMask { get; set; }
        private string UserIp { get; set; }

        /// <summary>
        /// Defines whether this request can be sent multiple times.
        /// </summary>
        public bool SupportsRetry
        {
            get { return true; }
        }

        #region IRequest Members

        /// <summary>
        /// The method to call
        /// </summary>
        /// <returns>
        /// A <see cref="Request"/>
        /// </returns>
        public IRequest On(string rpcName)
        {
            RPCName = rpcName;

            return this;
        }

        /// <summary>
        /// Sets the type of data that is expected to be returned from the request.
        /// 
        /// Defaults to Json.
        /// </summary>
        /// <param name="returnType">
        /// A <see cref="ReturnType"/>
        /// </param>
        /// <returns>
        /// A <see cref="Request"/>
        /// </returns>
        public IRequest Returning(ReturnType returnType)
        {
            ReturnType = returnType;
            return this;
        }

        /// <summary>
        /// Adds the parameters to the request.
        /// </summary>
        /// <returns>
        /// A <see cref="Request"/>
        /// </returns>
        public IRequest WithParameters(IDictionary<string, object> parameters)
        {
            Parameters = ParameterCollection.FromDictionary(parameters);
            return this;
        }

        /// <summary>
        /// Adds the parameters to the request.
        /// </summary>
        /// <returns>
        /// A <see cref="Request"/>
        /// </returns>
        public IRequest WithParameters(IEnumerable<KeyValuePair<string, string>> parameters)
        {
            Parameters = new ParameterCollection(parameters);
            return this;
        }

        /// <summary>
        /// Parses the specified querystring and adds these parameters to the request
        /// </summary>
        public IRequest WithParameters(string parameters)
        {
            Parameters = ParameterCollection.FromQueryString(parameters);
            return this;
        }

        /// <summary>
        /// Uses the string provied as the body of the request.
        /// </summary>
        public IRequest WithBody(string body)
        {
            Body = body;
            return this;
        }

        /// <summary>
        /// Uses the provided authenticator to add authentication information to this request.
        /// </summary>
        public IRequest WithAuthentication(IAuthenticator authenticator)
        {
            authenticator.ThrowIfNull("Authenticator");
            Authenticator = authenticator;
            return this;
        }

        /// <summary>
        /// Adds the developer key to this request.
        /// </summary>
        public IRequest WithKey(string key)
        {
            DeveloperKey = key;
            return this;
        }

        /// <summary>
        /// Sets the ETag-behavior of this request.
        /// </summary>
        public IRequest WithETagAction(ETagAction action)
        {
            ETagAction = action;
            return this;
        }

        /// <summary>
        /// Adds an ETag to this request.
        /// </summary>
        public IRequest WithETag(string etag)
        {
            ETag = etag;
            return this;
        }

        /// <summary>
        /// Executes a request given the configuration options supplied.
        /// </summary>
        /// <returns>
        /// A <see cref="Stream"/>
        /// </returns>
        public IResponse ExecuteRequest()
        {
            // Validate the input
            var validator = new MethodValidator(Method, Parameters);
            if (validator.ValidateAllParameters() == false)
            {
                return null;
            }

            WebRequest request = CreateWebRequest();

            // Try to execute the request.
            int tries = 0;
            double waitTime = RetryInitialWaitTime;
            while (true)
            {
                tries++;
                try
                {
                    // If we can get a valid response, return immediately.
                    WebResponse response = request.GetResponse();
                    return new Response(response);
                }
                catch (WebException ex)
                {
                    // Try to get an error response object.
                    RequestError error = null;
                    if (ex.Response != null)
                    {
                        IResponse errorResponse = new Response(ex.Response);
                        error = Service.DeserializeResponse<StandardResponse<object>>(errorResponse).Error;
                    }

                    // Try finding an error handler for this response.
                    bool isHandled = false;
                    if (SupportsRetry)
                    {
                        foreach (IErrorResponseHandler handler in GetErrorResponseHandlers())
                        {
                            if (handler.CanHandleErrorResponse(ex, error))
                            {
                                // The provided handler was able to handle this error. Retry sending the request.
                                handler.HandleErrorResponse(ex, error, request = CreateWebRequest());
                                isHandled = true;
                                break;
                            }
                        }
                    }

                    if (!isHandled || tries >= MaximumRetries)
                    {
                        // Retrieve additional information about the http response (if applicable).
                        HttpStatusCode status = 0;
                        HttpWebResponse httpResponse = ex.Response as HttpWebResponse;
                        if (httpResponse != null)
                        {
                            status = httpResponse.StatusCode;
                        }

                        // We were unable to handle the exception. Throw it.
                        throw new GoogleApiRequestException(Service, this, error, ex) { HttpStatusCode = status };
                    }

                    if (tries > 1) // The first retry occurs immediately.
                    {
                        Thread.Sleep((int)waitTime);
                        waitTime = waitTime * RetryWaitTimeIncreaseFactor;
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Returns all error response handlers associated with this request.
        /// </summary>
        [VisibleForTestOnly]
        internal IEnumerable<IErrorResponseHandler> GetErrorResponseHandlers()
        {
            // Check if the current authenticator can handle error responses.
            IErrorResponseHandler authenticator = Authenticator as IErrorResponseHandler;
            if (authenticator != null)
            {
                yield return authenticator;
            }
        }

        /// <summary>
        /// Given an API method, create the appropriate Request for it.
        /// </summary>
        public static IRequest CreateRequest(IService service, IMethod method)
        {
            switch (method.HttpMethod)
            {
                case GET:
                case PUT:
                case POST:
                case DELETE:
                case PATCH:
                    return new Request { Service = service, Method = method, BaseURI = service.BaseUri };
                default:
                    throw new NotSupportedException(
                        string.Format(
                            "The HttpMethod[{0}] of Method[{1}] in Service[{2}] was not supported", method.HttpMethod,
                            method.Name, service.Name));
            }
        }

        /// <summary>
        /// Sets the Application name on the UserAgent String.
        /// </summary>
        /// <param name="name">
        /// A <see cref="System.String"/>
        /// </param>
        public IRequest WithAppName(string name)
        {
            applicationName = name;
            return this;
        }

        /// <summary>
        /// Specifies the partial field mask of this method. 
        /// The response of this request will only contain the fields specified in this mask.
        /// </summary>
        /// <param name="mask">Selector specifying which fields to include in a partial response.</param>
        public IRequest WithFields(string mask)
        {
            FieldsMask = mask;
            return this;
        }

        /// <summary>
        /// IP address of the site where the request originates. Use this if you want to enforce per-user limits.
        /// </summary>
        public IRequest WithUserIp(string userIp)
        {
            UserIp = userIp;
            return this;
        }

        /// <summary>
        /// Builds the resulting Url for the whole request.
        /// </summary>
        [VisibleForTestOnly]
        internal Uri BuildRequestUrl()
        {
            string restPath = Method.RestPath;
            var queryParams = new List<string>();

            queryParams.Add(ReturnType == ReturnType.Json ? "alt=json" : "alt=atom");

            if (DeveloperKey.IsNotNullOrEmpty())
            {
                queryParams.Add("key=" + Uri.EscapeDataString(DeveloperKey));
            }

            if (FieldsMask.IsNotNullOrEmpty())
            {
                queryParams.Add("fields=" + Uri.EscapeDataString(FieldsMask));
            }

            if (UserIp.IsNotNullOrEmpty())
            {
                queryParams.Add("userIp=" + Uri.EscapeDataString(UserIp));
            }

            // Replace the substitution parameters.
            foreach (var parameter in Parameters)
            {
                IParameter parameterDefinition = Method.Parameters[parameter.Key];
                string value = parameter.Value;
                if (value.IsNullOrEmpty()) // If the parameter is present and has no value, use the default.
                {
                    value = parameterDefinition.DefaultValue;
                }
                switch (parameterDefinition.ParameterType)
                {
                    case "path":
                        restPath = restPath.Replace(String.Format("{{{0}}}", parameter.Key), value);
                        break;
                    case "query":
                        // If the parameter is optional and no value is given, don't add to url.
                        if (!parameterDefinition.IsRequired  && value.IsNullOrEmpty())
                        {
                            continue;
                        }

                        queryParams.Add(
                            Uri.EscapeDataString(parameterDefinition.Name) + "=" + Uri.EscapeDataString(value));
                        break;
                    default:
                        throw new NotSupportedException(
                            "Found an unsupported Parametertype [" + parameterDefinition.ParameterType + "]");
                }
            }

            // URL encode the parameters and append them to the URI.
            string path = restPath;
            if (queryParams.Count > 0)
            {
                path += "?" + String.Join("&", queryParams.ToArray());
            }

            return new Uri(BaseURI, path);
        }

        private static string GetReturnMimeType(ReturnType returnType)
        {
            switch (returnType)
            {
                case ReturnType.Atom:
                    return "application/atom+xml";
                case ReturnType.Json:
                    return "application/json";
                default:
                    throw new ArgumentOutOfRangeException("returnType", returnType, "Unknown return type");
            }
        }

        /// <summary>
        /// Returns the default ETagAction for a specific http verb.
        /// </summary>
        [VisibleForTestOnly]
        internal static ETagAction GetDefaultETagAction(string httpVerb)
        {
            switch (httpVerb)
            {
                default:
                    return ETagAction.Ignore;

                case GET: // Incoming data should only be updated if it has been changed on the server.
                    return ETagAction.IfNoneMatch;

                case PUT: // Outgoing data should only be commited if it hasn't been changed on the server.
                case POST:
                case PATCH:
                case DELETE:
                    return ETagAction.IfMatch;
            }
        }

        /// <summary>
        /// Creates the ready-to-send WebRequest containing all the data specified in this request class.
        /// </summary>
        [VisibleForTestOnly]
        internal WebRequest CreateWebRequest()
        {
            // Formulate the RequestUrl.
            requestUrl = BuildRequestUrl();

            // Create the request.
            HttpWebRequest request = Authenticator.CreateHttpWebRequest(Method.HttpMethod, requestUrl);

            // Insert the content type and user agent.
            request.ContentType = string.Format(
                "{0}; charset={1}", GetReturnMimeType(ReturnType), ContentCharset.HeaderName);
            string appName = FormatForUserAgent(ApplicationName);
            string apiVersion = FormatForUserAgent(ApiVersion);
            string platform = FormatForUserAgent(Environment.OSVersion.Platform.ToString());
            string platformVer = FormatForUserAgent(Environment.OSVersion.Version.ToString());
            request.UserAgent = String.Format(UserAgent, appName, apiVersion, platform, platformVer);
            
            // Add the E-tag header:
            if (!string.IsNullOrEmpty(ETag))
            {
                ETagAction action = this.ETagAction;
                if (action == ETagAction.Default)
                {
                    action = GetDefaultETagAction(request.Method);
                }

                switch (action)
                {
                    case ETagAction.IfMatch:
                        request.Headers[HttpRequestHeader.IfMatch] = ETag;
                        break;

                    case ETagAction.IfNoneMatch:
                        request.Headers[HttpRequestHeader.IfNoneMatch] = ETag;
                        break;
                }
            }

            // Check if compression is supported.
            if (Service.GZipEnabled)
            {
                request.UserAgent += GZipUserAgentSuffix;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }

            // Attach a body if a POST and there is something to attach.
            if (HttpMethodHasBody(request.Method))
            {
                if (!string.IsNullOrEmpty(Body))
                {
                    AttachBody(request);
                }
                else
                {
                    // Set the "Content-Length" header, which is required for every http method declaring a body. This 
                    // is required as e.g. the google servers will throw a "411 - Length required" error otherwise.
                    request.ContentLength = 0;
                }
            }

            return request;
        }

        [VisibleForTestOnly]
        internal string FormatForUserAgent(string fragment)
        {
            return fragment.Replace(' ', '_');
        }
        
        /// <summary>
        /// Adds the body of this request to the specified WebRequest.
        /// </summary>
        /// <remarks>Automatically applies GZip-compression if globally enabled.</remarks>
        [VisibleForTestOnly]
        internal void AttachBody(WebRequest request)
        {
            Stream bodyStream = request.GetRequestStream();

            // If enabled: Encapsulate in GZipStream.
            if (Service.GZipEnabled)
            {
                // Change the content encoding and apply a gzip filter.
                request.Headers.Add(HttpRequestHeader.ContentEncoding, GZipEncoding);
                bodyStream = new GZipStream(bodyStream, CompressionMode.Compress);
            }

            // Write data into the stream.
            using (bodyStream)
            {
                byte[] postBody = ContentCharset.GetBytes(Body);
                bodyStream.Write(postBody, 0, postBody.Length);
            }
        }

        /// <summary>
        /// Returns true if this http method can have a body.
        /// </summary>
        public static bool HttpMethodHasBody(string httpMethod)
        {
            switch (httpMethod)
            {
                case DELETE:
                case GET:
                    return false;
                case PATCH:
                case POST:
                case PUT:
                    return true;
                default:
                    throw new NotSupportedException(string.Format("The HttpMethod[{0}] is not supported", httpMethod));
            }
        }

        public override string ToString()
        {
            return string.Format("{0}({1} @ {2})", GetType(), Method.Name, BuildRequestUrl());
        }
    }
}