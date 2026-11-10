using UnityEngine;
using UnityEngine.InputSystem;

// Press E to open or close the crate lid. The lid swings smoothly instead of snapping.
// Put this on any object (for example the crate), then drag the lid's hinge object into "Lid Hinge".
// The hinge is an empty object placed at the back edge of the crate, with the lid as its child,
// so rotating the hinge swings the lid around that edge.
public class CrateLid : MonoBehaviour
{
    [Tooltip("The object that rotates. Its pivot must sit on the crate's hinge edge.")]
    public Transform lidHinge;

    [Tooltip("How far the lid swings open, in degrees.")]
    public float openAngle = 105f;

    [Tooltip("How many seconds the lid takes to open or close.")]
    public float openDuration = 0.8f;

    [Tooltip("The key that opens and closes the lid.")]
    public Key interactKey = Key.E;

    // True when the player last asked for the lid to be open.
    private bool _wantOpen;

    // 0 = fully closed, 1 = fully open. It moves a steady amount each frame.
    private float _progress;

    private Quaternion _closedRotation;
    private Quaternion _openRotation;

    private void Start()
    {
        // Remember the closed pose, and work out the open pose from it.
        // Euler(openAngle, 0, 0) tilts the lid around the hinge's own X axis.
        _closedRotation = lidHinge.localRotation;
        _openRotation = _closedRotation * Quaternion.Euler(openAngle, 0f, 0f);
    }

    private void Update()
    {
        // Keyboard.current is null if no keyboard is connected, so check it first.
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard[interactKey].wasPressedThisFrame)
            _wantOpen = !_wantOpen;

        // Move _progress toward 1 (open) or 0 (closed) at a steady speed.
        // Time.deltaTime keeps the speed the same at any frame rate.
        float goal = _wantOpen ? 1f : 0f;
        _progress = Mathf.MoveTowards(_progress, goal, Time.deltaTime / openDuration);

        // SmoothStep turns the steady 0..1 into an ease-in, ease-out curve:
        // the lid starts slowly, speeds up, then slows down as it lands.
        float eased = Mathf.SmoothStep(0f, 1f, _progress);
        lidHinge.localRotation = Quaternion.Slerp(_closedRotation, _openRotation, eased);
    }

    // A simple on-screen hint, drawn with the built-in OnGUI so it needs no Canvas.
    private void OnGUI()
    {
        string action = _wantOpen ? "close" : "open";
        GUI.Label(new Rect(20, Screen.height - 40, 300, 24), $"Press {interactKey} to {action} the crate");
    }
}
