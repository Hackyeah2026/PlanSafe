using System.Collections.Generic;
using System.Text.Json.Serialization;
using PlanSafe.Contracts.Models.Map;
using PlanSafe.Contracts.Models.Simulation;

namespace PlanSafe.Api;

[JsonSerializable(typeof(TargetAssignmentRequest))]
[JsonSerializable(typeof(TargetAssignmentResponse))]
[JsonSerializable(typeof(PublishPlanRequest))]
[JsonSerializable(typeof(PublishPlanResponse))]
[JsonSerializable(typeof(CheckInRequest))]
[JsonSerializable(typeof(CheckInResponse))]
[JsonSerializable(typeof(EvacuationPlanConfig))]
[JsonSerializable(typeof(TargetOccupancyUpdateRequest))]
[JsonSerializable(typeof(EvacuationTarget))]
[JsonSerializable(typeof(List<EvacuationTarget>))]
[JsonSerializable(typeof(IReadOnlyList<EvacuationTarget>))]
[JsonSerializable(typeof(ObstacleDto))]
[JsonSerializable(typeof(List<ObstacleDto>))]
[JsonSerializable(typeof(IReadOnlyList<ObstacleDto>))]
[JsonSerializable(typeof(GeoCoordinate))]
[JsonSerializable(typeof(List<GeoCoordinate>))]
[JsonSerializable(typeof(IReadOnlyList<GeoCoordinate>))]
[JsonSerializable(typeof(List<List<GeoCoordinate>>))]
[JsonSerializable(typeof(IReadOnlyList<List<GeoCoordinate>>))]
[JsonSerializable(typeof(IReadOnlyList<IReadOnlyList<GeoCoordinate>>))]
[JsonSerializable(typeof(TargetEvaluationDto))]
[JsonSerializable(typeof(List<TargetEvaluationDto>))]
[JsonSerializable(typeof(IReadOnlyList<TargetEvaluationDto>))]
[JsonSerializable(typeof(GenericActionResult))]
[JsonSerializable(typeof(CleanupActionResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, WriteIndented = false)]
public partial class EvacuationApiJsonContext : JsonSerializerContext
{
}
