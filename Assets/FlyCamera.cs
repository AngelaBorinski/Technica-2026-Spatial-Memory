using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

public class FlyCamera : MonoBehaviour
{
    public float moveSpeed = 1.5f;      // roughly walking speed, in m/s
    public float rotationSpeed = 90f;
    public float eyeHeight = 1.6f;

    private float pitch, yaw;
    private Vector3 feet;
    private bool onNavMesh;
    private StreamWriter logWriter;
    private bool endMarkerWritten;

    void Start()
    {
        string logPath = Path.Combine(Application.persistentDataPath, "character_log.csv");
        const string header = "record_type,timestamp_ms,x,y,z,pitch,yaw,roll";
        bool hasCurrentHeader = false;
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
        {
            using (StreamReader reader = new StreamReader(logPath))
                hasCurrentHeader = reader.ReadLine() == header;
        }

        logWriter = new StreamWriter(logPath, hasCurrentHeader);
        if (!hasCurrentHeader)
            logWriter.WriteLine(header);
        WriteRunMarker("START");

        Vector3 e = transform.eulerAngles;
        pitch = e.x > 180f ? e.x - 360f : e.x;
        yaw = e.y;

        // Start at the configured position, then snap to the nearest blue floor.
        Vector3 startPosition = new Vector3(11f, 1.2f, -2f);
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
            Debug.LogWarning("FlyCamera: no NavMesh near the camera. Move it above a blue floor area.");
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

    void WriteLogSample()
    {
        Vector3 p = transform.position;
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "DATA,{0:F3},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}",
            Time.realtimeSinceStartupAsDouble * 1000.0, p.x, p.y, p.z, pitch, yaw, 0f));
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
