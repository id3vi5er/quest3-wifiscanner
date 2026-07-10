using UnityEngine;

namespace WifiScanner
{
    /// <summary>
    /// "Geiger counter" audio feedback: the live RSSI of the connected AP
    /// drives the tick rate (and slightly the pitch) of a procedurally
    /// generated blip — the stronger the signal, the faster and higher the
    /// ticking. Uses SampleCollector.LastRssi, which is polled every frame
    /// even while recording is paused. Starts silent; toggled via the panel
    /// button "Audio". Created and wired by SceneBootstrap.
    /// </summary>
    public class GeigerCounter : MonoBehaviour
    {
        [Header("Wired by SceneBootstrap")]
        public SampleCollector collector;

        [Header("Mapping (dBm -> ticking)")]
        [Tooltip("RSSI at/below which the slowest beep rate is used (dBm).")]
        public int weakRssi = -95;
        [Tooltip("RSSI at/above which the fastest beep rate is used (dBm).")]
        public int strongRssi = -40;
        [Tooltip("Beeps per second at weakRssi.")]
        public float minBeepsPerSecond = 0.5f;
        [Tooltip("Beeps per second at strongRssi.")]
        public float maxBeepsPerSecond = 12f;
        [Tooltip("Playback pitch at weakRssi.")]
        public float minPitch = 0.9f;
        [Tooltip("Playback pitch at strongRssi.")]
        public float maxPitch = 1.5f;
        [Range(0f, 1f)] public float volume = 0.5f;

        public bool IsOn { get; private set; }

        private AudioSource _source;
        private AudioClip _blip;
        private float _nextBeepTime;

        private void Start()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f; // UI sound, not positional
            _blip = CreateBlipClip();
        }

        public void Toggle() => SetOn(!IsOn);

        public void SetOn(bool on)
        {
            IsOn = on;
            if (on) _nextBeepTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (!IsOn || collector == null || _source == null) return;

            int rssi = collector.LastRssi;
            if (rssi <= -126) return; // no reading yet — stay silent

            if (Time.unscaledTime < _nextBeepTime) return;
            float t = Mathf.InverseLerp(weakRssi, strongRssi, rssi);
            float rate = Mathf.Lerp(minBeepsPerSecond, maxBeepsPerSecond, t);
            _nextBeepTime = Time.unscaledTime + 1f / Mathf.Max(0.01f, rate);
            _source.pitch = Mathf.Lerp(minPitch, maxPitch, t);
            _source.PlayOneShot(_blip, volume);
        }

        /// <summary>Short sine blip with attack/decay envelope (no clicks).</summary>
        private static AudioClip CreateBlipClip()
        {
            const int sampleRate = 44100;
            const float durationSeconds = 0.06f;
            const float frequency = 1320f;
            int count = Mathf.RoundToInt(sampleRate * durationSeconds);
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                float time = i / (float)sampleRate;
                float attack = Mathf.Min(i / (sampleRate * 0.004f), 1f);
                float decay = 1f - i / (float)count;
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * time) *
                          attack * decay * decay;
            }
            var clip = AudioClip.Create("GeigerBlip", count, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
