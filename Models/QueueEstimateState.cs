namespace MediaFlux.Models;

/// <summary>Estimate progress is independent from whether a queue row is ready to encode.</summary>
public enum QueueEstimateState { Pending, Inspecting, Preliminary, Ready, Review, Measuring, Measured, Skip, Unavailable, Failed }
