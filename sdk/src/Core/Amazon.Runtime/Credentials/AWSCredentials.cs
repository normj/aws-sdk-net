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

using Amazon.Runtime.Identity;
using Amazon.Runtime.Internal.UserAgent;
using System.Collections.Generic;

namespace Amazon.Runtime
{
    /// <summary>
    /// Abstract class that represents a credentials object for AWS services.
    /// </summary>
    public abstract class AWSCredentials : BaseIdentity
    {
        /// <summary>
        /// Internal property that can be used to specify how this instance of AWS credentials were resolved.
        /// </summary>
        /// <remarks>
        /// Credential providers MUST add to this property to have their specific feature ID tracked.
        /// <para />
        /// If empty, no value will be included in the user agent header.
        /// </remarks>
        internal HashSet<UserAgentFeatureId> FeatureIdSources { get; set; } = new();

        /// <summary>
        /// Returns a copy of ImmutableCredentials
        /// </summary>
        /// <returns></returns>
        public abstract ImmutableCredentials GetCredentials();

        /// <summary>
        /// Called by AmazonServiceClient to validate the credential state
        /// on client construction.
        /// </summary>
        protected virtual void Validate() { }

        public virtual System.Threading.Tasks.Task<ImmutableCredentials> GetCredentialsAsync()
        {
            return System.Threading.Tasks.Task.FromResult<ImmutableCredentials>(this.GetCredentials());
        }

        /// <summary>
        /// Gets the credentials without allocating a task, when that returns exactly what awaiting
        /// <see cref="GetCredentialsAsync"/> would. Returns false when the caller must await
        /// <see cref="GetCredentialsAsync"/> instead.
        /// </summary>
        /// <remarks>
        /// The base implementation returns true only for the SDK's own credential types that use the default
        /// <see cref="GetCredentialsAsync"/> above, which is <see cref="GetCredentials"/> in a completed task. The exact
        /// type is checked because a derived class can override <see cref="GetCredentialsAsync"/>.
        /// </remarks>
        internal virtual bool TryGetCredentialsWithoutTask(out ImmutableCredentials credentials)
        {
            var type = GetType();
            if (type == typeof(BasicAWSCredentials) ||
                type == typeof(SessionAWSCredentials) ||
                type == typeof(EnvironmentVariablesAWSCredentials))
            {
                credentials = GetCredentials();
                return true;
            }

            credentials = null;
            return false;
        }
    }
}
