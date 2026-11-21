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

namespace Amazon.Runtime.Internal
{
    internal static class RequestExtensions
    {
        /// <summary>
        /// Returns the request's endpoint as a string, using <see cref="DefaultRequest.EndpointString"/>'s
        /// per-instance cache when available instead of allocating a new string via
        /// <see cref="System.Uri.ToString()"/> on every call. <see cref="DefaultRequest"/> is the only
        /// <see cref="IRequest"/> implementation in the SDK; any other implementation falls back to
        /// <c>Endpoint.ToString()</c> so behavior is unchanged.
        /// </summary>
        internal static string GetEndpointString(this IRequest request)
        {
            return request is DefaultRequest defaultRequest
                ? defaultRequest.EndpointString
                : request.Endpoint.ToString();
        }

        /// <summary>
        /// Returns <see cref="IRequest.SubResources"/> without creating it on a <see cref="DefaultRequest"/> that
        /// has never used it: null means there are no sub-resources. Any other <see cref="IRequest"/>
        /// implementation returns its property as before.
        /// </summary>
        internal static System.Collections.Generic.IDictionary<string, string> GetSubResourcesIfCreated(this IRequest request)
        {
            return request is DefaultRequest defaultRequest
                ? defaultRequest.SubResourcesIfCreated
                : request.SubResources;
        }

        /// <summary>
        /// Returns <see cref="IRequest.PathResources"/> without creating it on a <see cref="DefaultRequest"/> that
        /// has never used it: null means there are no path resources. Every consumer of path resources in the
        /// SDK treats null and empty the same way.
        /// </summary>
        internal static System.Collections.Generic.IDictionary<string, string> GetPathResourcesIfCreated(this IRequest request)
        {
            return request is DefaultRequest defaultRequest
                ? defaultRequest.PathResourcesIfCreated
                : request.PathResources;
        }

        /// <summary>
        /// Returns <see cref="IRequest.TrailingHeaders"/> without creating it on a <see cref="DefaultRequest"/> that
        /// has never used it: null means there are no trailing headers.
        /// </summary>
        internal static System.Collections.Generic.IDictionary<string, string> GetTrailingHeadersIfCreated(this IRequest request)
        {
            return request is DefaultRequest defaultRequest
                ? defaultRequest.TrailingHeadersIfCreated
                : request.TrailingHeaders;
        }
    }
}
