using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

// Keyboard version for testing on the laptop (no headset).
// Put this on a plain Camera, and turn the XR Origin off while using it.
//  - WASD: move     - Arrow keys: look around
// Writes the same character_log.csv format as FlyCameraMeta, so Lia's analysis
// and VideoGenerator can read both.
public class FlyCamera : MonoBehaviour
{
    public float moveSpeed = 1.5f;      // roughly walking speed, in m/s
    public float rotationSpeed = 90f;
    public float eyeHeight = 1.6f;

    [Tooltip("Where the camera starts (it snaps to the nearest blue floor). Same as FlyCameraMeta.")]
    public Vector3 startPosition = new Vector3(11f, 1.2f, -2f);

    [Tooltip("Save to disk every N frames so data survives a crash (60 = about once a second).")]
    public int flushEveryNFrames = 60;

    private const string Header = "record_type,timestamp_ms,x,y,z,pitch,yaw,roll";

    private float pitch, yaw;
    private Vector3 feet;
    private bool onNavMesh;
    private StreamWriter logWriter;
    private bool endMarkerWritten;

    void Start()
    {
        string logPath = Path.Combine(Application.persistentDataPath, "character_log.csv");
        Debug.Log("Logging to: " + logPath);

        // Append to an existing file only if it has the current header; otherwise start it over
        bool hasCurrentHeader = false;
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
        {
            using (StreamReader reader = new StreamReader(logPath))
                hasCurrentHeader = reader.ReadLine() == Header;
        }

        logWriter = new StreamWriter(logPath, hasCurrentHeader);
        if (!hasCurrentHeader)
            logWriter.WriteLine(Header);
        WriteRunMarker("START");

        Vector3 e = transform.eulerAngles;
        pitch = e.x > 180f ? e.x - 360f : e.x;
        yaw = e.y;

        // Start at the configured position, then snap to the nearest blue floor
        transform.position = startPosition;
        Vector3 below = transform.position - Vector3.up * eyeHeight;
        if (NavMesh.SamplePosition(below, out var hit, 10f, NavMesh.AllAreas))
        {
            feet = hit.position;
            onNavMesh = true;
            transform.position = feet + Vector3.up * eyeHeight;
        }
        else
        {
            Debug.LogWarning("FlyCamera: no NavMesh near the start position. Move it above a blue floor area.");
        }
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null)
        {
            WriteLogSample();
            return;
        }

        // Look (arrow keys)
        float turn = (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f);
        float look = (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);
        yaw += turn * rotationSpeed * Time.deltaTime;
        pitch = Mathf.Clamp(pitch - look * rotationSpeed * Time.deltaTime, -85f, 85f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        // Move (WASD), horizontal only
        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        Vector3 step = (right * x + forward * z).normalized * moveSpeed * Time.deltaTime;

        if (!onNavMesh)
        {
            transform.position += step;
            WriteLogSample();
            return;
        }

        // Try to step; if that leaves the NavMesh, snap back to its nearest edge.
        // This blocks walls, lets you slide along them, and follows stairs.
        if (NavMesh.SamplePosition(feet + step, out var floor, 0.5f, NavMesh.AllAreas))
            feet = floor.position;

        transform.position = feet + Vector3.up * eyeHeight;
        WriteLogSample();
    }

    // x and z are the floor-plan coordinates for the top-down map; y is eye height.
    // roll is always 0 on the keyboard version (the headset version records real head tilt).
    void WriteLogSample()
    {
        if (logWriter == null) return;

        Vector3 p = transform.position;
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "DATA,{0:F3},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}",
            Time.realtimeSinceStartupAsDouble * 1000.0, p.x, p.y, p.z, pitch, yaw, 0f));

        if (flushEveryNFrames > 0 && Time.frameCount % flushEveryNFrames == 0)
            logWriter.Flush();
    }

    void OnDestroy()
    {
        if (logWriter != null)
        {
            WriteEndMarker();
            logWriter.Flush();
            logWriter.Dispose();
            logWriter = null;
        }
    }

    void OnApplicationQuit()
    {
        WriteEndMarker();
        if (logWriter != null)
            logWriter.Flush();
    }

    // START/END rows: same 8 columns as DATA rows, with position/rotation left empty
    void WriteRunMarker(string marker)
    {
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F3},,,,,,",
            marker, Time.realtimeSinceStartupAsDouble * 1000.0));
        logWriter.Flush();
    }

    void WriteEndMarker()
    {
        if (logWriter != null && !endMarkerWritten)
        {
            WriteRunMarker("END");
            endMarkerWritten = true;
        }
    }
}