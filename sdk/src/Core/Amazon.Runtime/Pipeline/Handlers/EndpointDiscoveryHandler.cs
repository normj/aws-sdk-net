/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Net;
using Amazon.Util;

namespace Amazon.Runtime.Internal
{
    /// <summary>
    /// This handler resolves the endpoint to be used for the current request.
    /// </summary>
    public class EndpointDiscoveryHandler : PipelineHandler
    {
        //The status code returned from a service request when an invalid endpoint is used.
        private const int INVALID_ENDPOINT_EXCEPTION_STATUSCODE = 421;

        // The discovered endpoint comes from the client's endpoint cache, so consecutive requests almost always use
        // the same address string. The last address and the Uri parsed from it are kept so an unchanged address is
        // not parsed again. Uri instances are immutable, so sharing one between requests, clients and threads is
        // safe; the entry is replaced as a whole.
        private static DiscoveredEndpointUri _lastDiscoveredEndpointUri;

        private sealed class DiscoveredEndpointUri
        {
            internal DiscoveredEndpointUri(string address, Uri uri)
            {
                Address = address;
                Uri = uri;
            }

            internal string Address { get; }
            internal Uri Uri { get; }
        }

        /// <summary>
        /// Calls pre invoke logic before calling the next handler 
        /// in the pipeline.
        /// </summary>
        /// <param name="executionContext">The execution context which contains both the
        /// requests and response context.</param>
        public override void InvokeSync(IExecutionContext executionContext)
        {
            var requestContext = executionContext.RequestContext;
            var regionalEndpoint = requestContext.Request.Endpoint;
            
            ImmutableCredentials immutableCredentials = null;
            if (requestContext.Identity is AWSCredentials credentials)
            {
                immutableCredentials = credentials.GetCredentials();
            }

            PreInvoke(executionContext, immutableCredentials);

            try
            {
                base.InvokeSync(executionContext);
                return;
            }
            catch (Exception exception)
            {        
                if (IsInvalidEndpointException(exception))
                {
                    EvictCacheKeyForRequest(requestContext, regionalEndpoint, immutableCredentials);
                }

                throw;
            }
        }

        /// <summary>
        /// Calls pre invoke logic before calling the next handler 
        /// in the pipeline.
        /// </summary>
        /// <typeparam name="T">The response type for the current request.</typeparam>
        /// <param name="executionContext">The execution context, it contains the
        /// request and response context.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        public override async System.Threading.Tasks.Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            var requestContext = executionContext.RequestContext;
            var regionalEndpoint = requestContext.Request.Endpoint;

            ImmutableCredentials immutableCredentials = null;
            if (requestContext.Identity is AWSCredentials credentials)
            {
                immutableCredentials = await credentials.GetCredentialsAsync().ConfigureAwait(false);
            }

            PreInvoke(executionContext, immutableCredentials);

            try
            {
                return await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);    
            }
            catch (Exception exception)
            {
                var capturedException = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                if (IsInvalidEndpointException(capturedException.SourceException))
                {
                    EvictCacheKeyForRequest(requestContext, regionalEndpoint, immutableCredentials);
                }

                capturedException.Throw();
            }

            throw new AmazonClientException("Neither a response was returned nor an exception was thrown in the Runtime EndpointDiscoveryResolver.");
        }

        /// <summary>
        /// Resolves the endpoint to be used for the current request
        /// before invoking the next handler.
        /// </summary>
        /// <param name="executionContext">The execution context, it contains the request and response context.</param>        
        /// <param name="credentials">The AWS credentials for the account making the service call.</param>
        protected static void PreInvoke(IExecutionContext executionContext, ImmutableCredentials credentials)
        {
            DiscoverEndpoints(executionContext.RequestContext, false, credentials);
        }

        public static void EvictCacheKeyForRequest(IRequestContext requestContext, Uri regionalEndpoint, ImmutableCredentials credentials)
        {
            DiscoverEndpoints(requestContext, true, credentials);
            requestContext.Request.Endpoint = regionalEndpoint;
        }

        public static void DiscoverEndpoints(IRequestContext requestContext, bool evictCacheKey, ImmutableCredentials credentials)
        {
            var discoveryEndpoints = ProcessEndpointDiscovery(requestContext, evictCacheKey, requestContext.Request.Endpoint, credentials);
            if (discoveryEndpoints != null)
            {
                foreach (var endpoint in discoveryEndpoints)
                {
                    //A null address can occur if this endpoint does not require endpoint discovery
                    //and we couldn't get an endpoint back during an asynchronous discovery
                    //attempt. The null address endpoint will be evicted after 60 seconds but will
                    //prevent multiple server requests during this time.
                    if (endpoint.Address == null)
                    {
                        continue;
                    }

                    //Only the first endpoint should be used currently
                    requestContext.Request.Endpoint = GetDiscoveredEndpointUri(endpoint.Address);
                    break;
                }
            }
        }

        /// <summary>
        /// Returns <c>new Uri(address)</c>, or the Uri parsed for the previous request when <paramref name="address"/>
        /// is the same string.
        /// </summary>
        internal static Uri GetDiscoveredEndpointUri(string address)
        {
            var cached = System.Threading.Volatile.Read(ref _lastDiscoveredEndpointUri);
            if (cached != null && string.Equals(cached.Address, address, StringComparison.Ordinal))
            {
                return cached.Uri;
            }

            var uri = new Uri(address);
            System.Threading.Volatile.Write(ref _lastDiscoveredEndpointUri, new DiscoveredEndpointUri(address, uri));
            return uri;
        }

        private static IEnumerable<DiscoveryEndpointBase> ProcessEndpointDiscovery(IRequestContext requestContext, bool evictCacheKey, Uri evictUri, ImmutableCredentials credentials)
        {
            var options = requestContext.Options;

            if (options.EndpointDiscoveryMarshaller != null && options.EndpointOperation != null && credentials != null)
            {
                //Endpoint discovery is supported by this operation and we have an endpoint operation available to use                
                var endpointDiscoveryData = options.EndpointDiscoveryMarshaller.Marshall(requestContext.OriginalRequest);
                var operationName = string.Empty;
                if (endpointDiscoveryData.Identifiers != null && endpointDiscoveryData.Identifiers.Count > 0)
                {
                    operationName = AWSSDKUtils.ExtractOperationName(requestContext.RequestName);
                }
                return options.EndpointOperation(new EndpointOperationContext(credentials.AccessKey, operationName, endpointDiscoveryData, evictCacheKey, evictUri));
            }

            return null;
        }

        private static bool IsInvalidEndpointException(Exception exception)
        {
            var serviceException = exception as AmazonServiceException;
            if (serviceException != null && serviceException.StatusCode == (HttpStatusCode)INVALID_ENDPOINT_EXCEPTION_STATUSCODE)
            {
                return true;
            }

            return false;
        }
    }
}