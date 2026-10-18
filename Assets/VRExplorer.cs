using System.Globalization;
using System.IO;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

// VR version of FlyCamera. Put this on the XR Origin.
//  - Left thumbstick: walk in the direction you're looking
//  - Right thumbstick: snap turn (flick left/right)
//  - Real-life walking also works; both are kept on the baked NavMesh
//  - Logs head position + head rotation to the same CSV format as FlyCamera
[RequireComponent(typeof(XROrigin))]
public class VRExplorer : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 1.5f;           // roughly walking speed, in m/s
    public float snapTurnDegrees = 30f;
    [Range(0.05f, 0.9f)] public float stickDeadzone = 0.2f;

    [Header("Data logging")]
    [Tooltip("Change this before each participant's session. Each ID gets its own CSV file.")]
    public string participantId = "P01";

    [Tooltip("Save to disk every N frames so data survives a crash (72 = about once a second on Quest).")]
    public int flushEveryNFrames = 72;

    private const string Header = "record_type,timestamp_ms,x,y,z,pitch,yaw";

    private XROrigin origin;
    private Transform head;
    private InputAction moveAction, turnAction;
    private bool turnReady = true;
    private bool onNavMesh;
    private Vector3 lastFeet;
    private StreamWriter logWriter;
    private bool endMarkerWritten;

    void Awake()
    {
        origin = GetComponent<XROrigin>();
        head = origin.Camera.transform;

        // Camera height = the participant's real head height above the floor
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;

        moveAction = new InputAction("Move", InputActionType.Value,
            binding: "<XRController>{LeftHand}/{Primary2DAxis}", expectedControlType: "Vector2");
        turnAction = new InputAction("Turn", InputActionType.Value,
            binding: "<XRController>{RightHand}/{Primary2DAxis}", expectedControlType: "Vector2");
    }

    void OnEnable()
    {
        moveAction.Enable();
        turnAction.Enable();
    }

    void OnDisable()
    {
        moveAction.Disable();
        turnAction.Disable();
    }

    void Start()
    {
        // One file per participant, e.g. character_log_P01.csv
        string logPath = Path.Combine(Application.persistentDataPath, $"character_log_{participantId}.csv");
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
    }

    // LateUpdate so the headset's tracked position for this frame is already applied
    void LateUpdate()
    {
        // Snap turn: one turn per flick of the right stick
        float turnX = turnAction.ReadValue<Vector2>().x;
        if (turnReady && Mathf.Abs(turnX) > 0.7f)
        {
            origin.RotateAroundCameraUsingOriginUp(Mathf.Sign(turnX) * snapTurnDegrees);
            turnReady = false;
        }
        else if (Mathf.Abs(turnX) < 0.3f)
        {
            turnReady = true;
        }

        // Thumbstick walking, relative to where the head is facing, horizontal only
        Vector2 stick = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
        Vector3 step = Vector3.zero;
        if (stick.magnitude > stickDeadzone)
        {
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(head.right, Vector3.up).normalized;
            step = (forward * stick.y + right * stick.x) * moveSpeed * Time.deltaTime;
        }

        ConstrainToNavMesh(step);
        WriteLogSample();
    }

    // Moves the whole rig so the point under the head stays on the blue NavMesh.
    // Blocks walls, slides along them, follows stairs/ramps, and also catches
    // the participant physically walking into a wall.
    void ConstrainToNavMesh(Vector3 step)
    {
        Vector3 feet = new Vector3(head.position.x, transform.position.y, head.position.z);

        if (!onNavMesh)
        {
            // First frame: snap onto the nearest floor
            if (NavMesh.SamplePosition(feet, out var start, 10f, NavMesh.AllAreas))
            {
                transform.position += start.position - feet;
                lastFeet = start.position;
                onNavMesh = true;
            }
            else
            {
                Debug.LogWarning("VRExplorer: no NavMesh near the XR Origin. Move it above a blue floor area.");
                transform.position += step;
            }
            return;
        }

        Vector3 target = lastFeet; // fallback: stay where we were
        if (NavMesh.SamplePosition(feet + step, out var floor, 0.5f, NavMesh.AllAreas))
            target = floor.position;

        transform.position += target - feet;
        lastFeet = target;
    }

    // x and z are the floor-plan coordinates for the top-down map; y is head height.
    // pitch/yaw are where the participant is looking (head rotation).
    void WriteLogSample()
    {
        if (logWriter == null) return;

        Vector3 p = head.position;
        Vector3 e = head.eulerAngles;
        float pitch = e.x > 180f ? e.x - 360f : e.x;
        float yaw = e.y;

        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "DATA,{0:F3},{1:R},{2:R},{3:R},{4:R},{5:R}",
            Time.realtimeSinceStartupAsDouble * 1000.0, p.x, p.y, p.z, pitch, yaw));

        if (flushEveryNFrames > 0 && Time.frameCount % flushEveryNFrames == 0)
            logWriter.Flush();
    }

    // On Quest, apps are usually paused rather than quit, so save when that happens
    void OnApplicationPause(bool paused)
    {
        if (paused && logWriter != null)
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

    // START/END rows: same 7 columns as DATA rows, with position/rotation left empty
    void WriteRunMarker(string marker)
    {
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F3},,,,,",
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
