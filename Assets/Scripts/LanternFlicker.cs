using UnityEngine;

// Makes a light flicker like a real flame, so the lantern feels like it is still burning.
// Put this on the same GameObject as the Light (or assign one in the Inspector).
public class LanternFlicker : MonoBehaviour
{
    [Tooltip("The light to flicker. If empty, uses the Light on this object.")]
    public Light flameLight;

    [Tooltip("How much of the brightness can change. 0.2 = up to 20% brighter or dimmer.")]
    [Range(0f, 0.6f)]
    public float flickerAmount = 0.2f;

    [Tooltip("How fast the flame wobbles.")]
    public float flickerSpeed = 2.5f;

    [Tooltip("Optional glowing flame shape. It grows and shrinks with the light and sways a little.")]
    public Transform flame;

    // The brightness set in the Inspector. We flicker around this value.
    private float _baseIntensity;

    // The flame's size set in the Inspector. We stretch it around this value.
    private Vector3 _flameBaseScale;

    // A random offset so two lanterns in one scene don't flicker in sync.
    private float _seed;

    private void Start()
    {
        if (flameLight == null) flameLight = GetComponent<Light>();
        if (flameLight == null)
        {
            Debug.LogWarning("LanternFlicker needs a Light.", this);
            enabled = false; // stop Update from running without a light
            return;
        }
        _baseIntensity = flameLight.intensity;
        _seed = Random.value * 100f;
        if (flame != null) _flameBaseScale = flame.localScale;
    }

    private void Update()
    {
        // Perlin noise gives a smooth, random-looking value between 0 and 1.
        // That feels more like a flame than Random.value, which jumps around every frame.
        float noise = Mathf.PerlinNoise(_seed, Time.time * flickerSpeed);

        // Turn 0..1 into -1..1, then scale it by how much flicker we want.
        float offset = (noise - 0.5f) * 2f * flickerAmount;
        flameLight.intensity = _baseIntensity * (1f + offset);

        if (flame != null)
        {
            // A brighter flame is also a taller, thinner flame. Same noise as the light,
            // so the glow and the flame always move together.
            float height = 1f + offset;
            float width = 1f - offset * 0.5f;
            flame.localScale = new Vector3(_flameBaseScale.x * width, _flameBaseScale.y * height, _flameBaseScale.z * width);

            // Lean a few degrees left and right, using a second noise value so it does not
            // just copy the brightness.
            float lean = (Mathf.PerlinNoise(_seed + 50f, Time.time * flickerSpeed * 0.7f) - 0.5f) * 12f;
            flame.localRotation = Quaternion.Euler(0f, 0f, lean);
        }
    }
}
