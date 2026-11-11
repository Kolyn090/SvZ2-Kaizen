using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class OutputLogFile : SingletonSpawningMonoBehaviour<OutputLogFile>
{
	private const string kErrorFileKey = "OUTPUTLOGFILE_ERROR_FILE";

	// Disabled by default: the legacy Glu upload endpoint is obsolete and rejected
	// by Unity's insecure-HTTP policy. Existing scene/prefab serialization does not
	// contain this field (the component is not serialized in any asset), so it can
	// only be enabled deliberately in the Inspector or in code.
	[SerializeField]
	private bool enableRemoteLogUploads = false;

	public string uploadURL = "http://kw-macmini650.glu.com:8888/log_file_grab/grab.php";

	private StreamWriter writer;

	private string logPath;

	private string uploadInProgress = string.Empty;

	private DateTime pauseTime;

	public bool Initialized { get; private set; }

	private void Start()
	{
		if (GeneralConfig.IsLive)
		{
			UnityEngine.Object.DestroyImmediate(this);
		}
		else
		{
			Initialize();
		}
	}

	private void OnApplicationPause(bool pause)
	{
	}

	protected override void OnDestroy()
	{
		if (Application.isEditor && SingletonSpawningMonoBehaviour<OutputLogFile>.Instance == this)
		{
			if (writer != null)
			{
				writer.Flush();
				writer.Close();
				writer = null;
			}
			Application.RegisterLogCallback(null);
		}
		base.OnDestroy();
	}

	public void Initialize()
	{
		if (!Initialized)
		{
			Initialized = true;
			Logging.Initialize();
			SetLogPath(Logging.LogFilePath);
		}
	}

	private void BeginLog()
	{
		string text = Application.persistentDataPath + "/Logs";
		Directory.CreateDirectory(text);
		string arg = string.Format("Log_{0}_{1}.txt", DateTime.Now.ToShortDateString().Replace("/", "."), DateTime.Now.ToLongTimeString().Replace(":", "."));
		logPath = string.Format("{0}/{1}", text, arg);
		FileStream stream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
		writer = new StreamWriter(stream);
		writer.AutoFlush = true;
		SetLogPath(logPath);
	}

	private void SaveLog()
	{
		if (writer != null)
		{
			writer.Flush();
		}
	}

	private void EndLog()
	{
		if (writer != null)
		{
			writer.Flush();
			writer.Close();
			writer = null;
			if (!string.IsNullOrEmpty(logPath))
			{
				byte[] data = File.ReadAllBytes(logPath);
				StartCoroutine(UploadLog(logPath, data));
				logPath = null;
			}
		}
	}

	private void HandleLog(string logString, string stackTrace, LogType type)
	{
		if (logString == null)
		{
			return;
		}
		if (writer == null)
		{
			BeginLog();
		}
		if (writer == null)
		{
			return;
		}
		writer.WriteLine(logString);
		if (type != LogType.Log)
		{
			if (stackTrace != null)
			{
				writer.WriteLine("###STACK###");
				writer.Write(stackTrace);
				writer.WriteLine("###########");
			}
			if (type != LogType.Warning)
			{
				SaveLog();
			}
		}
	}

	private IEnumerator UploadLog(string logFilePath, byte[] data)
	{
		if (!enableRemoteLogUploads)
		{
			// Remote uploads disabled: no request, no uploadInProgress state, and the
			// pending-upload PlayerPrefs record is intentionally left untouched.
			yield break;
		}
		if (!logFilePath.Equals(uploadInProgress))
		{
			uploadInProgress = logFilePath;
			WWWForm postForm = new WWWForm();
			postForm.AddField("gameName", GeneralConfig.GameName);
			postForm.AddField("deviceName", SystemInfo.deviceName);
			postForm.AddBinaryData("contents", data, logFilePath, "text/plain");
			using (UnityWebRequest upload = UnityWebRequest.Post(uploadURL, postForm))
			{
				yield return upload.SendWebRequest();
				if (upload.error == null)
				{
				}
				string errorFile = PlayerPrefs.GetString("OUTPUTLOGFILE_ERROR_FILE");
				if (logFilePath.Equals(errorFile))
				{
					PlayerPrefs.SetString("OUTPUTLOGFILE_ERROR_FILE", string.Empty);
				}
				uploadInProgress = string.Empty;
			}
		}
	}

	private void SetLogPath(string path)
	{
		if (!string.IsNullOrEmpty(path))
		{
			string @string = PlayerPrefs.GetString("OUTPUTLOGFILE_ERROR_FILE");
			if (File.Exists(@string) && !string.IsNullOrEmpty(@string) && !path.Equals(@string))
			{
				byte[] data = File.ReadAllBytes(@string);
				StartCoroutine(UploadLog(@string, data));
			}
			PlayerPrefs.SetString("OUTPUTLOGFILE_ERROR_FILE", path);
		}
	}
}
