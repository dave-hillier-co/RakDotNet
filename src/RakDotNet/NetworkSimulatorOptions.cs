namespace RakDotNet;

/// <summary>ApplyNetworkSimulator, implemented as a transport decorator, with a seed for reproducible loss.</summary>
public sealed record NetworkSimulatorOptions(float PacketLoss, TimeSpan MinExtraPing, TimeSpan ExtraPingVariance, int Seed = 0);
