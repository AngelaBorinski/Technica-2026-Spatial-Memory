using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using UnityEngine;

public class VideoGenerator : MonoBehaviour
{
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
#endif

    [Header("Replay")]
    public Transform playbackTarget;
    public Camera captureCamera;
    public Behaviour movementControllerToDisable;
    public int framesPerSecond = 30;
    [Tooltip("Run number starting at 1 for the oldest. Set to 0 to use the latest completed run.")]
    public int runNumber = 0;

    [Header("Files")]
    [Tooltip("CSV path or filename. Leave blank to use character_log.csv in Application.persistentDataPath.")]
    public string csvFilePath = "";
    public string videoFileName = "character_replay.mp4";
    public string ffmpegExecutable = "ffmpeg";

    private struct Sample
    {
        public double timestampMs;
        public Vector3 position;
        public float pitch;
        public float yaw;
        public float roll;
    }

    private Process encoder;
    private Texture2D readbackTexture;
    private RenderTexture renderTexture;
    private bool recording;

    void Start()
    {
        try
        {
            PromptForCsvPath();

            if (playbackTarget == null)
                throw new InvalidOperationException("Assign the character or camera transform to Playback Target.");
            if (captureCamera == null)
                captureCamera = Camera.main;
            if (captureCamera == null)
                throw new InvalidOperationException("Assign a camera or tag the scene camera as MainCamera.");
            if (framesPerSecond < 1)
                throw new InvalidOperationException("Frames Per Second must be at least 1.");

            string csvPath = ResolveCsvPath();
            List<List<Sample>> runs = ReadCompletedRuns(csvPath);
            if (runs.Count == 0)
                throw new InvalidOperationException("The CSV contains no completed run with DATA rows.");

            int selectedRun = PromptForRunNumber(runs.Count);
            List<Sample> samples = runs[selectedRun];

            int width = captureCamera.targetTexture != null ? captureCamera.targetTexture.width : captureCamera.pixelWidth;
            int height = captureCamera.targetTexture != null ? captureCamera.targetTexture.height : captureCamera.pixelHeight;
            width = Mathf.Max(2, width - width % 2);
            height = Mathf.Max(2, height - height % 2);

            string videoPath = Path.Combine(Application.persistentDataPath, videoFileName);
            StartEncoder(videoPath, width, height);

            renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            readbackTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            recording = true;
            StartCoroutine(ReplayAndCapture(samples, width, height));
        }
        catch (Exception exception)
        {
            Debug.LogError("VideoGenerator: " + exception.Message, this);
            Cleanup();
        }
    }

    int PromptForRunNumber(int runCount)
    {
        int latestIndex = runCount - 1;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        Console.Write("Run number (1-" + runCount + ", Enter = latest): ");
        string enteredRun = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(enteredRun))
        {
            if (int.TryParse(enteredRun, out int requestedRun) && requestedRun >= 1 && requestedRun <= runCount)
                return requestedRun - 1;

            Console.WriteLine("Invalid run number; using latest run " + runCount + ".");
        }
        return latestIndex;
#else
        if (runNumber >= 1 && runNumber <= runCount)
            return runNumber - 1;

        return latestIndex;
#endif
    }

    void PromptForCsvPath()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        AllocConsole();
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        string defaultPath = string.IsNullOrWhiteSpace(csvFilePath)
            ? Path.Combine(Application.persistentDataPath, "character_log.csv")
            : csvFilePath;
        Console.Write("CSV file path [" + defaultPath + "]: ");
        string enteredPath = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(enteredPath))
            csvFilePath = enteredPath.Trim().Trim('"');
#else
        Debug.Log("CSV path prompt is available in a Windows standalone build. In the Editor, set CSV File Path in the Inspector.", this);
#endif
    }

    string ResolveCsvPath()
    {
        if (string.IsNullOrWhiteSpace(csvFilePath))
            return Path.Combine(Application.persistentDataPath, "character_log.csv");

        if (Path.IsPathRooted(csvFilePath))
            return csvFilePath;

        return Path.Combine(Application.persistentDataPath, csvFilePath);
    }

    List<List<Sample>> ReadCompletedRuns(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("CSV file not found", path);

        List<Sample> currentRun = null;
        List<List<Sample>> completedRuns = new List<List<Sample>>();
        string[] lines = File.ReadAllLines(path);

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string[] columns = line.Split(',');
            if (columns[0] == "START")
            {
                currentRun = new List<Sample>();
            }
            else if (columns[0] == "END")
            {
                if (currentRun != null && currentRun.Count > 0)
                    completedRuns.Add(currentRun);
                currentRun = null;
            }
            else if (columns[0] == "DATA" && currentRun != null && columns.Length >= 7)
            {
                if (!double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double timestamp) ||
                    !float.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(columns[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !float.TryParse(columns[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch) ||
                    !float.TryParse(columns[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float yaw))
                {
                    continue;
                }

                float roll = 0f;
                if (columns.Length >= 8 &&
                    !float.TryParse(columns[7], NumberStyles.Float, CultureInfo.InvariantCulture, out roll))
                    continue;

                currentRun.Add(new Sample
                {
                    timestampMs = timestamp,
                    position = new Vector3(x, y, z),
                    pitch = pitch,
                    yaw = yaw,
                    roll = roll
                });
            }
        }

        if (completedRuns.Count == 0)
            throw new InvalidDataException("No completed START/DATA/END run was found in the CSV.");

        return completedRuns;
    }

    void StartEncoder(string outputPath, int width, int height)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            Arguments = "-y -f rawvideo -pixel_format rgba -video_size " + width + "x" + height +
                        " -framerate " + framesPerSecond + " -i - -an -vf vflip -c:v libx264 -preset ultrafast" +
                        " -pix_fmt yuv420p " + QuoteArgument(outputPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        Process newEncoder = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        newEncoder.ErrorDataReceived += (sender, eventArgs) =>
        {
            if (!string.IsNullOrEmpty(eventArgs.Data))
                UnityEngine.Debug.Log("FFmpeg: " + eventArgs.Data, this);
        };
        newEncoder.Start();
        encoder = newEncoder;
        encoder.BeginErrorReadLine();
    }

    IEnumerator ReplayAndCapture(List<Sample> samples, int width, int height)
    {
        Transform target = playbackTarget;
        Vector3 originalPosition = target.position;
        Quaternion originalRotation = target.rotation;
        RenderTexture originalCameraTarget = captureCamera.targetTexture;
        Behaviour controller = movementControllerToDisable;
        if (controller == null)
            controller = target.GetComponent<FlyCamera>();
        bool controllerWasEnabled = controller != null && controller.enabled;
        if (controllerWasEnabled)
            controller.enabled = false;

        int previousCaptureFramerate = Time.captureFramerate;
        captureCamera.targetTexture = renderTexture;
        Time.captureFramerate = framesPerSecond;

        try
        {
            double firstTimestamp = samples[0].timestampMs;
            double durationMs = Math.Max(0.0, samples[samples.Count - 1].timestampMs - firstTimestamp);
            int frameCount = Math.Max(1, (int)Math.Ceiling(durationMs * framesPerSecond / 1000.0) + 1);
            int sampleIndex = 0;
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();

            for (int frame = 0; frame < frameCount; frame++)
            {
                double replayTimeMs = firstTimestamp + frame * 1000.0 / framesPerSecond;
                while (sampleIndex < samples.Count - 2 && samples[sampleIndex + 1].timestampMs < replayTimeMs)
                    sampleIndex++;

                Sample a = samples[sampleIndex];
                Sample b = samples[Math.Min(sampleIndex + 1, samples.Count - 1)];
                double sampleSpan = b.timestampMs - a.timestampMs;
                float blend = sampleSpan > 0.0
                    ? Mathf.Clamp01((float)((replayTimeMs - a.timestampMs) / sampleSpan))
                    : 0f;

                target.position = Vector3.Lerp(a.position, b.position, blend);
                target.rotation = Quaternion.Euler(
                    Mathf.LerpAngle(a.pitch, b.pitch, blend),
                    Mathf.LerpAngle(a.yaw, b.yaw, blend),
                    Mathf.LerpAngle(a.roll, b.roll, blend));

                yield return endOfFrame;
                CaptureFrame(width, height);
            }

            encoder.StandardInput.BaseStream.Flush();
            encoder.StandardInput.Close();
            encoder.WaitForExit();
            if (encoder.ExitCode != 0)
                Debug.LogError("FFmpeg exited with code " + encoder.ExitCode + ". Check the Console for its output.", this);
            else
                Debug.Log("Video saved to " + Path.Combine(Application.persistentDataPath, videoFileName), this);
        }
        finally
        {
            target.position = originalPosition;
            target.rotation = originalRotation;
            captureCamera.targetTexture = originalCameraTarget;
            Time.captureFramerate = previousCaptureFramerate;
            if (controllerWasEnabled)
                controller.enabled = true;
            Cleanup();
        }
    }

    void CaptureFrame(int width, int height)
    {
        captureCamera.Render();
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = renderTexture;
        readbackTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        readbackTexture.Apply(false, false);
        RenderTexture.active = previousActive;

        Color32[] pixels = readbackTexture.GetPixels32();
        byte[] rgba = new byte[pixels.Length * 4];
        for (int i = 0, j = 0; i < pixels.Length; i++)
        {
            rgba[j++] = pixels[i].r;
            rgba[j++] = pixels[i].g;
            rgba[j++] = pixels[i].b;
            rgba[j++] = pixels[i].a;
        }

        encoder.StandardInput.BaseStream.Write(rgba, 0, rgba.Length);
    }

    string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    void Cleanup()
    {
        recording = false;
        if (encoder != null)
        {
            if (!encoder.HasExited)
            {
                try { encoder.StandardInput.Close(); } catch { }
                encoder.WaitForExit();
            }
            encoder.Dispose();
            encoder = null;
        }

        if (readbackTexture != null)
        {
            Destroy(readbackTexture);
            readbackTexture = null;
        }
        if (renderTexture != null)
        {
            renderTexture.Release();
            Destroy(renderTexture);
            renderTexture = null;
        }
    }

    void OnDestroy()
    {
        if (recording && encoder != null && !encoder.HasExited)
        {
            try { encoder.StandardInput.Close(); } catch { }
            encoder.WaitForExit();
        }
    }
}
