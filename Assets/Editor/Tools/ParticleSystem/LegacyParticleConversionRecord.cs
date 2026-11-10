using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Persistent audit record for one legacy particle system conversion
/// performed by LegacyParticleUpdaterV2.
///
/// One record is created per converted legacy emitter and stored under the
/// current records folder (see kRecordFolders below). It is the single source
/// of truth for what was converted, what was approximated, and what legacy
/// properties could not be represented in the new ParticleSystem.
///
/// Compatibility target: Unity 2017.4.40f1 (Editor script).
/// </summary>
public enum LegacyParticleConversionStatus
{
	Unconverted = 0,
	Converted = 1,
	Validated = 2,   // visually compared in-engine (A/B) by a human
	SignedOff = 3,   // accepted by a human; safe for cutover
	Reverted = 4,    // conversion was removed by "Revert Selected Conversions"
	Blocked = 5,     // conversion refused by a guard rule (reason in warnings)
}

[Serializable]
public class LegacyParticleFieldEntry
{
	public string field;
	public string value;
}

public class LegacyParticleConversionRecord : ScriptableObject
{
	public LegacyParticleConversionStatus status;

	/// <summary>Asset path of the scene or prefab that contains the legacy emitter ("&lt;untitled&gt;" for unsaved scenes).</summary>
	public string sourceAssetPath = "";

	/// <summary>Full transform hierarchy path of the GameObject holding the legacy ParticleEmitter.</summary>
	public string sourceObjectPath = "";

	/// <summary>Type name of the legacy emitter (e.g. EllipsoidParticleEmitter).</summary>
	public string legacyEmitterType = "";

	/// <summary>Full transform hierarchy path of the created ParticleSystem holder GameObject.</summary>
	public string targetObjectPath = "";

	public string createdAt = "";
	public string unityVersion = "";

	/// <summary>Every serialized property that could be read from the legacy emitter/animator/renderer (raw dump for audit).</summary>
	public List<LegacyParticleFieldEntry> legacyFields = new List<LegacyParticleFieldEntry>();

	/// <summary>Property mappings believed to be exact (still requires in-engine verification).</summary>
	public List<string> exactMappings = new List<string>();

	/// <summary>Property mappings that are approximations; each entry names the legacy property and why it is approximate.</summary>
	public List<string> approximations = new List<string>();

	/// <summary>Legacy properties with no usable Shuriken representation. Nothing was written for these.</summary>
	public List<string> unsupported = new List<string>();

	/// <summary>Guard results, runtime-reference warnings and other items a reviewer must see.</summary>
	public List<string> warnings = new List<string>();

	/// <summary>
	/// Deterministic record file name from source asset path and object path.
	/// Stable across Unity sessions (no instance IDs), the base of the
	/// duplicate-conversion guards.
	/// </summary>
	public static string BuildRecordName(string assetPath, string objectPath)
	{
		return "_" + Sanitize(assetPath) + "__" + Sanitize(objectPath) + ".asset";
	}

	/// <summary>Replaces every character outside [A-Za-z0-9._-] with '_' (safe for asset names on Windows + macOS).</summary>
	public static string Sanitize(string s)
	{
		if (string.IsNullOrEmpty(s))
			return "untitled";

		char[] chars = s.ToCharArray();
		for (int i = 0; i < chars.Length; i++)
		{
			char c = chars[i];
			bool ok = (c >= 'a' && c <= 'z') ||
				(c >= 'A' && c <= 'Z') ||
				(c >= '0' && c <= '9') ||
				c == '_' || c == '.' || c == '-';
			if (!ok)
				chars[i] = '_';
		}
		return new string(chars);
	}

	// ----------------------------------------------------------------------
	// RECORD FOLDER RESOLUTION - the ONLY place that knows where records live.
	// If the tooling moves again, edit this list and nothing else.
	//
	// Candidates are tried IN ORDER when locating an existing record; new
	// records are always created in the first candidate (DefaultRecordsFolder).
	//   [0] current home, next to the converter tooling
	//       (Assets/Editor/Tools/ParticleSystem/Records)
	//   [1] pre-reorganization home, kept so records created there still load
	//       (Assets/LegacyParticleConversion/Records)
	// ----------------------------------------------------------------------
	static readonly string[] kRecordFolders = new string[]
	{
		"Assets/Editor/Tools/ParticleSystem/Records",
		"Assets/LegacyParticleConversion/Records",
	};

	/// <summary>Folder where NEW records are created (first candidate above).</summary>
	public static string DefaultRecordsFolder
	{
		get { return kRecordFolders[0]; }
	}

	/// <summary>
	/// Asset path of the record with the given name: the first candidate
	/// folder that actually contains it, or the default folder when it does
	/// not exist anywhere yet (so a subsequent CreateAsset targets the
	/// current home). Relative asset paths resolve against the project root,
	/// which is the editor working directory.
	/// </summary>
	public static string ResolveRecordPath(string recordName)
	{
		for (int i = 0; i < kRecordFolders.Length; i++)
		{
			string candidate = kRecordFolders[i] + "/" + recordName;
			if (File.Exists(candidate))
				return candidate;
		}
		return DefaultRecordsFolder + "/" + recordName;
	}

	/// <summary>
	/// Creates the default records folder (including any missing parents)
	/// if it does not exist yet. Returns false and logs on failure.
	/// </summary>
	public static bool EnsureDefaultRecordsFolder()
	{
		try
		{
			string[] parts = DefaultRecordsFolder.Split('/');
			string current = parts[0]; // "Assets"
			for (int i = 1; i < parts.Length; i++)
			{
				string next = current + "/" + parts[i];
				if (!AssetDatabase.IsValidFolder(next))
					AssetDatabase.CreateFolder(current, parts[i]);
				current = next;
			}
			return true;
		}
		catch (Exception ex)
		{
			Debug.LogError("LegacyParticleConversionRecord: could not create records folder: " + ex.Message);
			return false;
		}
	}
}
