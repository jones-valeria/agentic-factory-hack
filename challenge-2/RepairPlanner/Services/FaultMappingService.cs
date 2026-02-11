using System;
using System.Collections.Generic;

namespace RepairPlanner.Services;

public interface IFaultMappingService
{
    IReadOnlyList<string> GetRequiredSkills(string faultType);
    IReadOnlyList<string> GetRequiredParts(string faultType);
}

public sealed class FaultMappingService : IFaultMappingService
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FaultToSkills =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["curing_temperature_excessive"] = new List<string>
            {
                "tire_curing_press",
                "temperature_control",
                "instrumentation",
                "electrical_systems",
                "plc_troubleshooting",
                "mold_maintenance",
            },
            ["curing_cycle_time_deviation"] = new List<string>
            {
                "tire_curing_press",
                "plc_troubleshooting",
                "mold_maintenance",
                "bladder_replacement",
                "hydraulic_systems",
                "instrumentation",
            },
            ["building_drum_vibration"] = new List<string>
            {
                "tire_building_machine",
                "vibration_analysis",
                "bearing_replacement",
                "alignment",
                "precision_alignment",
                "drum_balancing",
                "mechanical_systems",
            },
            ["ply_tension_excessive"] = new List<string>
            {
                "tire_building_machine",
                "tension_control",
                "servo_systems",
                "precision_alignment",
                "sensor_alignment",
                "plc_programming",
            },
            ["extruder_barrel_overheating"] = new List<string>
            {
                "tire_extruder",
                "temperature_control",
                "rubber_processing",
                "screw_maintenance",
                "instrumentation",
                "electrical_systems",
                "motor_drives",
            },
            ["low_material_throughput"] = new List<string>
            {
                "tire_extruder",
                "rubber_processing",
                "screw_maintenance",
                "motor_drives",
                "temperature_control",
            },
            ["high_radial_force_variation"] = new List<string>
            {
                "tire_uniformity_machine",
                "data_analysis",
                "measurement_systems",
                "tire_building_machine",
                "tire_curing_press",
            },
            ["load_cell_drift"] = new List<string>
            {
                "tire_uniformity_machine",
                "load_cell_calibration",
                "measurement_systems",
                "sensor_alignment",
                "instrumentation",
            },
            ["mixing_temperature_excessive"] = new List<string>
            {
                "banbury_mixer",
                "temperature_control",
                "rubber_processing",
                "instrumentation",
                "electrical_systems",
                "mechanical_systems",
            },
            ["excessive_mixer_vibration"] = new List<string>
            {
                "banbury_mixer",
                "vibration_analysis",
                "bearing_replacement",
                "alignment",
                "mechanical_systems",
                "preventive_maintenance",
            },
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> FaultToParts =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["curing_temperature_excessive"] = new List<string> { "TCP-HTR-4KW", "GEN-TS-K400" },
            ["curing_cycle_time_deviation"] = new List<string> { "TCP-BLD-800", "TCP-SEAL-200" },
            ["building_drum_vibration"] = new List<string> { "TBM-BRG-6220" },
            ["ply_tension_excessive"] = new List<string> { "TBM-LS-500N", "TBM-SRV-5KW" },
            ["extruder_barrel_overheating"] = new List<string> { "EXT-HTR-BAND", "GEN-TS-K400" },
            ["low_material_throughput"] = new List<string> { "EXT-SCR-250", "EXT-DIE-TR" },
            ["high_radial_force_variation"] = new List<string>(),
            ["load_cell_drift"] = new List<string> { "TUM-LC-2KN", "TUM-ENC-5000" },
            ["mixing_temperature_excessive"] = new List<string> { "BMX-TIP-500", "GEN-TS-K400" },
            ["excessive_mixer_vibration"] = new List<string> { "BMX-BRG-22320", "BMX-SEAL-DP" },
        };

    private static readonly IReadOnlyList<string> DefaultSkills = new List<string> { "general_maintenance" };
    private static readonly IReadOnlyList<string> DefaultParts = new List<string>();

    public IReadOnlyList<string> GetRequiredSkills(string faultType)
    {
        if (string.IsNullOrWhiteSpace(faultType))
        {
            return DefaultSkills;
        }

        return FaultToSkills.TryGetValue(faultType, out var skills) ? skills : DefaultSkills;
    }

    public IReadOnlyList<string> GetRequiredParts(string faultType)
    {
        if (string.IsNullOrWhiteSpace(faultType))
        {
            return DefaultParts;
        }

        return FaultToParts.TryGetValue(faultType, out var parts) ? parts : DefaultParts;
    }
}
