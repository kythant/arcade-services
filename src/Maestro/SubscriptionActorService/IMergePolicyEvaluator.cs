// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Maestro.Contracts;
using Maestro.Data.Models;
using Maestro.MergePolicyEvaluation;
using Microsoft.DotNet.DarcLib;

namespace SubscriptionActorService;

public interface IMergePolicyEvaluator
{
    Task<MergePolicyEvaluationResults> EvaluateAsync(
        IPullRequest pr,
        IRemote darc,
        IReadOnlyList<MergePolicyDefinition> policyDefinitions);
}
