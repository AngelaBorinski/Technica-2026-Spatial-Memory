using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

// Aliases so the XR types don't clash with the Input System's InputDevice / CommonUsages
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRInputDevices = UnityEngine.XR.InputDevices;
using XRNode = UnityEngine.XR.XRNode;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

// Put this on the XR Origin (don't also enable VRExplorer on it).
// Left stick: forward/back = move where you're looking, left/right = smooth turn.
// A/X button (or Space): mark the current checkpoint when standing within checkpointRadius of it.
public class FlyCameraMeta : MonoBehaviour
{
    public float moveSpeed = 1.5f;      // roughly walking speed, in m/s
    public float turnSpeed = 30f;        // smooth yaw speed, in degrees per second
    [Min(0.01f)] public float turnSmoothTime = 0.2f;
    [Range(0f, 0.9f)] public float joystickDeadzone = 0.15f;

    [Tooltip("Save to disk every N frames so data survives a crash (72 = about once a second on Quest).")]
    public int flushEveryNFrames = 72;

    [Tooltip("Size of the checkpoint prompt shown in front of the participant. Raise/lower if it's too small/big.")]
    public float promptCharacterSize = 0.005f;

    [Tooltip("How close (in meters, measured on the floor plan) the participant must be to a checkpoint when pressing A/X.")]
    public float checkpointRadius = 1.25f;

    [Tooltip("How long the 'Found it!' / 'Not here yet' message stays up, in seconds.")]
    public float feedbackSeconds = 2f;

    private static readonly Vector2[] Checkpoints =
    {
        new Vector2(3.7f, 0f),
        new Vector2(1.7f, -7.2f),
        new Vector2(9.7f, 0.3f),
        new Vector2(2.9f, -3.2f),
        new Vector2(4.8f, -7.7f)
    };

    private static readonly string[] CheckpointNames =
    {
        "Coffee Machine",
        "Bathtub",
        "Fireplace",
        "American Flag",
        "Dresses"
    };

    private float pitch, yaw, roll;
    private Vector3 feet;
    private Vector3 startFeet;
    private Vector3 startPosition;
    private bool onNavMesh;
    private int currentCheckpointIndex;
    private bool markButtonWasPressed;
    private float smoothedTurnInput;
    private float turnInputVelocity;
    private StreamWriter logWriter;
    private bool endMarkerWritten;
    private XRInputDevice leftController;
    private XRInputDevice rightController;
    private XRInputDevice headsetDevice;
    private Quaternion headsetRotation = Quaternion.identity;
    private Camera headCamera;
    private TextMesh promptText;
    private string feedbackMessage;
    private float feedbackUntil;

    void Start()
    {
        headCamera = GetComponentInChildren<Camera>();

        // Prompt and NavMesh placement come FIRST, so a logging problem can never
        // leave the participant without prompts or walking through walls.
        CreatePrompt();
        UpdatePrompt();

        // Place the XR Origin, then snap its root to the nearest blue floor.
        startPosition = new Vector3(11f, 1.2f, -2f);
        transform.position = startPosition;
        startFeet = transform.position;
        TrySnapToNavMesh();

        OpenLog();
    }

    // Snaps the rig onto the nearest blue floor. Called at start and retried every
    // frame until it succeeds, so the player is never left moving freely.
    bool TrySnapToNavMesh()
    {
        if (!NavMesh.SamplePosition(startPosition, out var hit, 10f, NavMesh.AllAreas))
            return false;

        feet = hit.position;
        startFeet = feet;
        onNavMesh = true;
        transform.position = feet;
        startPosition = transform.position;
        return true;
    }

    void OpenLog()
    {
        string logPath = Path.Combine(Application.persistentDataPath, "character_log.csv");
        Debug.Log("Logging to: " + logPath);
        const string header = "record_type,timestamp_ms,x,y,z,pitch,yaw,roll";

        try
        {
            bool hasCurrentHeader = false;
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
            {
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                    hasCurrentHeader = reader.ReadLine() == header;
            }

            var file = new FileStream(logPath, hasCurrentHeader ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.Read);
            logWriter = new StreamWriter(file);
            if (!hasCurrentHeader)
                logWriter.WriteLine(header);
            WriteRunMarker("START");
        }
        catch (System.Exception e)
        {
            logWriter = null;
            Debug.LogError("FlyCameraMeta: could not open the log file, so this session is NOT being recorded. " +
                           "Is FlyCamera (or another copy of this script) also running? " + e.Message);
            ShowFeedback("WARNING: data is not being recorded");
        }
    }

    void Update()
    {
        if (!leftController.isValid)
            leftController = XRInputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        if (!rightController.isValid)
            rightController = XRInputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        if (!headsetDevice.isValid)
            headsetDevice = XRInputDevices.GetDeviceAtXRNode(XRNode.Head);

        Vector2 stick = Vector2.zero;
        if (leftController.isValid)
            leftController.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out stick);

        if (headsetDevice.isValid &&
            headsetDevice.TryGetFeatureValue(XRCommonUsages.deviceRotation, out Quaternion trackedRotation))
            headsetRotation = trackedRotation;

        bool markButtonPressed = false;
        if (leftController.isValid &&
            leftController.TryGetFeatureValue(XRCommonUsages.primaryButton, out bool leftPrimaryPressed))
            markButtonPressed |= leftPrimaryPressed;
        if (rightController.isValid &&
            rightController.TryGetFeatureValue(XRCommonUsages.primaryButton, out bool rightPrimaryPressed))
            markButtonPressed |= rightPrimaryPressed;
        if (Keyboard.current != null && Keyboard.current.spaceKey.isPressed)
            markButtonPressed = true;

        bool markButtonJustPressed = markButtonPressed && !markButtonWasPressed;
        markButtonWasPressed = markButtonPressed;
        if (markButtonJustPressed && TryCompleteCurrentCheckpoint())
        {
            WriteLogSample();
            return;
        }

        // Clear the "Found it!" / "Not here yet" message after a moment
        if (feedbackMessage != null && Time.time > feedbackUntil)
        {
            feedbackMessage = null;
            UpdatePrompt();
        }

        // Only horizontal input turns the XR Origin. Vertical input does not affect rotation.
        float turnInput = Mathf.Abs(stick.x) > joystickDeadzone
            ? Mathf.Sign(stick.x) * Mathf.InverseLerp(joystickDeadzone, 1f, Mathf.Abs(stick.x))
            : 0f;
        smoothedTurnInput = Mathf.SmoothDamp(smoothedTurnInput, turnInput,
            ref turnInputVelocity, turnSmoothTime);
        transform.Rotate(Vector3.up,
            smoothedTurnInput * turnSpeed * Time.deltaTime, Space.World);

        // Forward/back movement on the vertical axis, in the direction the head faces.
        float movementInput = Mathf.Abs(stick.y) > joystickDeadzone
            ? Mathf.Sign(stick.y) * Mathf.InverseLerp(joystickDeadzone, 1f, Mathf.Abs(stick.y))
            : 0f;
        Vector3 forward = transform.rotation * headsetRotation * Vector3.forward;
        forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
        Vector3 step = forward * movementInput * moveSpeed * Time.deltaTime;

        // Never move freely: if we're not on the NavMesh yet, keep trying to snap onto it
        if (!onNavMesh && !TrySnapToNavMesh())
        {
            WriteLogSample();
            return;
        }

        // Project each movement step onto nearby NavMesh geometry.
        if (NavMesh.SamplePosition(feet + step, out var floor, 0.5f, NavMesh.AllAreas))
            feet = floor.position;

        transform.position = feet;
        WriteLogSample();
    }

    // Where the participant's head actually is (includes real-life steps/leans)
    Vector3 HeadPosition()
    {
        return headCamera != null ? headCamera.transform.position : transform.position;
    }

    bool TryCompleteCurrentCheckpoint()
    {
        if (currentCheckpointIndex >= Checkpoints.Length)
            return false;

        Vector3 head = HeadPosition();
        Vector2 position = new Vector2(head.x, head.z);
        float distance = Vector2.Distance(position, Checkpoints[currentCheckpointIndex]);
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "FlyCameraMeta: pressed at ({0:F2}, {1:F2}), {2:F2} m from {3}",
            head.x, head.z, distance, CheckpointNames[currentCheckpointIndex]));

        if (distance > checkpointRadius)
        {
            ShowFeedback("Not here yet - keep looking");
            return false;
        }

        WriteCheckpointCompletion(currentCheckpointIndex + 1);
        currentCheckpointIndex++;
        feet = startFeet;
        transform.position = startPosition;
        ShowFeedback("Found it!");
        return true;
    }

    void ShowFeedback(string message)
    {
        feedbackMessage = message;
        feedbackUntil = Time.time + feedbackSeconds;
        UpdatePrompt();
    }

    // Draws each checkpoint in the Scene view (not in the game) so you can check
    // that it sits next to its item and on/near the blue NavMesh.
    void OnDrawGizmos()
    {
        for (int i = 0; i < Checkpoints.Length; i++)
        {
            Vector3 c = new Vector3(Checkpoints[i].x, transform.position.y, Checkpoints[i].y);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(c, checkpointRadius);
            Gizmos.DrawLine(c, c + Vector3.up * 2f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(c + Vector3.up * 2.1f, (i + 1) + ": " + CheckpointNames[i]);
#endif
        }
    }

    // OnGUI only draws on the PC screen, never inside the headset, so the prompt
    // is a 3D text that floats in front of the participant's eyes instead.
    void CreatePrompt()
    {
        if (headCamera == null) return;

        var go = new GameObject("CheckpointPrompt");
        go.transform.SetParent(headCamera.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.25f, 1.0f);

        promptText = go.AddComponent<TextMesh>();
        promptText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        go.GetComponent<MeshRenderer>().sharedMaterial = promptText.font.material;
        promptText.anchor = TextAnchor.MiddleCenter;
        promptText.alignment = TextAlignment.Center;
        promptText.fontSize = 200;
        promptText.characterSize = promptCharacterSize;
        promptText.fontStyle = FontStyle.Bold;
        promptText.color = Color.white;
    }

    void UpdatePrompt()
    {
        if (promptText == null) return;
        string task = currentCheckpointIndex < Checkpoints.Length
            ? string.Format(CultureInfo.InvariantCulture, "Find: {0} ({1}/{2})\nPress A or X when you're next to it",
                CheckpointNames[currentCheckpointIndex], currentCheckpointIndex + 1, Checkpoints.Length)
            : "All checkpoints completed - thank you!";
        promptText.text = feedbackMessage != null ? feedbackMessage + "\n" + task : task;
    }

    float NormalizeAngle(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }

    // Logs head position and head rotation in WORLD space, so yaw includes
    // the joystick turning as well as real head turns.
    void WriteLogSample()
    {
        if (logWriter == null) return;

        Vector3 p = HeadPosition();
        Quaternion worldRotation = headCamera != null
            ? headCamera.transform.rotation
            : transform.rotation * headsetRotation;
        Vector3 angles = worldRotation.eulerAngles;
        pitch = NormalizeAngle(angles.x);
        yaw = NormalizeAngle(angles.y);
        roll = NormalizeAngle(angles.z);

        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture, "DATA,{0:F3},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R}",
            Time.realtimeSinceStartupAsDouble * 1000.0, p.x, p.y, p.z, pitch, yaw, roll));

        if (flushEveryNFrames > 0 && Time.frameCount % flushEveryNFrames == 0)
            logWriter.Flush();
    }

    void WriteCheckpointCompletion(int checkpointNumber)
    {
        if (logWriter == null) return;
        logWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "CHECKPOINT_{0}_COMPLETED,{1:F3},,,,,,",
            checkpointNumber, Time.realtimeSinceStartupAsDouble * 1000.0));
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

    void WriteRunMarker(string marker)
    {
        if (logWriter == null) return;
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
