// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Maestro.Web.Api.v2018_07_16.Models;

public class Channel
{
    public Channel([NotNull] Data.Models.Channel other)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        Id = other.Id;
        Name = other.Name;
        Classification = other.Classification;
    }

    public int Id { get; }

    [Required]
    public string Name { get; }

    [Required]
    public string Classification { get; }

    public List<ReleasePipeline> ReleasePipelines { get; }
}
