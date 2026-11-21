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
using Amazon.Runtime.Internal.UserAgent;

namespace Amazon.Runtime
{
    /// <summary>
    /// Base class for request used by some of the services.
    /// </summary>
    public abstract partial class AmazonWebServiceRequest : Amazon.Runtime.Internal.IAmazonWebServiceRequest
    {
        // The event lock and the user agent details are created on first use. Applications usually create a
        // new request object for every call, and most never add a BeforeRequestEvent handler or a request-level
        // user agent component, so neither is needed.
        private object _lock;
        private UserAgentDetails _userAgentDetails;

        internal RequestEventHandler mBeforeRequestEvent;

        // Same reason as the existing CA1033 suppression for RequestState: an internal interface member,
        // previously an auto-property, which the analyzer did not flag.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1033:Interface methods should be callable by child types")]
        UserAgentDetails Amazon.Runtime.Internal.IAmazonWebServiceRequest.UserAgentDetails
        {
            get
            {
                // Every caller must see the same instance, so only the first one created is published.
                return System.Threading.Volatile.Read(ref _userAgentDetails)
                    ?? System.Threading.Interlocked.CompareExchange(ref _userAgentDetails, new UserAgentDetails(), null)
                    ?? _userAgentDetails;
            }
        }

        /// <summary>
        /// The request's user agent details, or null if nothing has accessed them yet (so there is nothing in them).
        /// </summary>
        internal UserAgentDetails UserAgentDetailsIfCreated => System.Threading.Volatile.Read(ref _userAgentDetails);

        private object Lock
        {
            get
            {
                // Every caller must lock the same object, so only the first one created is published.
                return System.Threading.Volatile.Read(ref _lock)
                    ?? System.Threading.Interlocked.CompareExchange(ref _lock, new object(), null)
                    ?? _lock;
            }
        }

        internal event RequestEventHandler BeforeRequestEvent
        {
            add
            {
                lock (Lock)
                {
                    mBeforeRequestEvent += value;
                }
            }
            remove
            {
                lock (Lock)
                {
                    mBeforeRequestEvent -= value;
                }
            }
        }

        EventHandler<StreamTransferProgressArgs> Amazon.Runtime.Internal.IAmazonWebServiceRequest.StreamUploadProgressCallback { get; set; }

        private Dictionary<string, object> requestState = null;
        Dictionary<string, object> Amazon.Runtime.Internal.IAmazonWebServiceRequest.RequestState
        {
            get
            {
                if (requestState == null)
                {
                    requestState = new Dictionary<string, object>();
                }
                return requestState;
            }
        }

        protected AmazonWebServiceRequest()
        {
        }

        void Amazon.Runtime.Internal.IAmazonWebServiceRequest.AddBeforeRequestHandler(RequestEventHandler handler)
        {
            BeforeRequestEvent += handler;
        }

        void Amazon.Runtime.Internal.IAmazonWebServiceRequest.RemoveBeforeRequestHandler(RequestEventHandler handler)
        {
            BeforeRequestEvent -= handler;
        }

        internal void FireBeforeRequestEvent(object sender, RequestEventArgs args)
        {
            if (mBeforeRequestEvent != null)
                mBeforeRequestEvent(sender, args);
        }
    }
}
