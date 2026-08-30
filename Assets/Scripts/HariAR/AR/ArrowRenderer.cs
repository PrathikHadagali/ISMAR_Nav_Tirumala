// ArrowRenderer.cs
// ---------------------------------------------------------------------------
// A single Google Maps Live View-style 3D floating AR arrow.
//
// Features:
//   • Procedurally-generated 3D chevron mesh with bevelled edges and thickness.
//   • Vibrantly glowing PBR/unlit material with customizable emission.
//   • Distance-based scale curve (Near = 1.0, Far = 0.7, Very Far = 0.5).
//   • Smooth positional lerping and rotational slerping (never teleports).
//   • Smooth proximity alpha fading (fades out within 1.5m of camera or when passed).
//   • Built-in subtle floating hover animation.
// ---------------------------------------------------------------------------

using UnityEngine;

namespace HariAR.AR
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ArrowRenderer : MonoBehaviour
    {
        [Header("Appearance")]
        public Color arrowColor = new Color(0.12f, 0.52f, 1.0f, 0.95f);       // Vivid Live View Blue
        public Color emissionColor = new Color(0.08f, 0.40f, 0.95f, 1.0f);    // Azure Glow
        public Color arrivedColor = new Color(0.20f, 0.95f, 0.40f, 0.95f);     // Green on Arrival

        [Header("Smoothing")]
        public float positionLerpSpeed = 10f;
        public float rotationSlerpSpeed = 12f;
        public float scaleLerpSpeed = 8f;
        public float alphaFadeSpeed = 6f;

        [Header("Hover Animation")]
        public float hoverAmplitude = 0.035f;
        public float hoverFrequency = 2.0f;

        // Transform Targets
        public Vector3 TargetPosition { get; set; }
        public Quaternion TargetRotation { get; set; }
        public Vector3 TargetScale { get; set; }
        public float TargetAlpha { get; set; }

        public int PathIndex { get; set; } = -1;
        public bool IsInUse { get; private set; }

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Material _material;
        MaterialPropertyBlock _propBlock;
        Transform _transform;

        float _currentAlpha = 0f;
        float _hoverPhase;
        bool _initialized;

        static Mesh _sharedChevronMesh;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        void Awake()
        {
            EnsureInitialized();
        }

        void EnsureInitialized()
        {
            if (_initialized) return;

            _transform = transform;
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            if (_sharedChevronMesh == null)
            {
                _sharedChevronMesh = GenerateChevronMesh();
            }

            _meshFilter.sharedMesh = _sharedChevronMesh;
            _material = CreateArrowMaterial(arrowColor, emissionColor);
            _meshRenderer.material = _material;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            _propBlock = new MaterialPropertyBlock();
            _hoverPhase = Random.Range(0f, Mathf.PI * 2f);

            _initialized = true;
        }

        /// <summary>
        /// Generates a solid 3D Chevron Arrow mesh with top face, bottom face, and angled side walls.
        /// Formed along the XZ plane, pointing forward (+Z), with thickness along Y.
        /// </summary>
        public static Mesh GenerateChevronMesh()
        {
            var mesh = new Mesh { name = "LiveView_3D_Chevron" };

            // Chevron 2D top-down profile in XZ (metres):
            // Outer width = 1.3m, Length = 0.85m, Notch depth = 0.38m, Wing thickness = 0.32m
            const float halfW = 0.65f;       // Half width (Left/Right tip: ±0.65)
            const float tipZ = 0.50f;        // Forward tip (+Z)
            const float innerTipZ = 0.18f;   // Inner forward V notch (+Z)
            const float backWingZ = -0.35f;  // Rear wing back corner (-Z)
            const float innerBackZ = -0.05f; // Inner back notch
            const float halfY = 0.06f;       // Half thickness = 0.12m total thickness

            // 6 profile points forming a 2D chevron polygon:
            // 0: Forward outer tip
            // 1: Right outer wing
            // 2: Right inner wing corner
            // 3: Inner rear notch
            // 4: Left inner wing corner
            // 5: Left outer wing
            Vector2[] profile =
            {
                new Vector2(0f, tipZ),
                new Vector2(halfW, backWingZ),
                new Vector2(halfW - 0.22f, backWingZ - 0.08f),
                new Vector2(0f, innerTipZ),
                new Vector2(-(halfW - 0.22f), backWingZ - 0.08f),
                new Vector2(-halfW, backWingZ)
            };

            int pc = profile.Length;
            var vertices = new Vector3[pc * 2];
            var normals = new Vector3[pc * 2];
            var uvs = new Vector2[pc * 2];

            // Top vertices (Y = +halfY)
            for (int i = 0; i < pc; i++)
            {
                vertices[i] = new Vector3(profile[i].x, halfY, profile[i].y);
                normals[i] = Vector3.up;
                uvs[i] = new Vector2((profile[i].x / (halfW * 2f)) + 0.5f, (profile[i].y / (tipZ - backWingZ)) + 0.5f);
            }

            // Bottom vertices (Y = -halfY)
            for (int i = 0; i < pc; i++)
            {
                vertices[pc + i] = new Vector3(profile[i].x, -halfY, profile[i].y);
                normals[pc + i] = Vector3.down;
                uvs[pc + i] = uvs[i];
            }

            // Top Face Triangles (Polygon triangulation for 6-point chevron)
            // Triangles: (0,1,2), (0,2,3), (0,3,4), (0,4,5)
            int[] topTris = {
                0, 1, 2,
                0, 2, 3,
                0, 3, 4,
                0, 4, 5
            };

            // Bottom Face Triangles (reversed winding)
            int[] bottomTris = new int[topTris.Length];
            for (int i = 0; i < topTris.Length; i += 3)
            {
                bottomTris[i] = pc + topTris[i];
                bottomTris[i + 1] = pc + topTris[i + 2];
                bottomTris[i + 2] = pc + topTris[i + 1];
            }

            // Side Wall Quads (2 triangles per profile edge)
            int sideTriCount = pc * 6;
            int[] sideTris = new int[sideTriCount];
            int stIdx = 0;
            for (int i = 0; i < pc; i++)
            {
                int next = (i + 1) % pc;
                int topA = i;
                int topB = next;
                int botA = pc + i;
                int botB = pc + next;

                sideTris[stIdx++] = topA;
                sideTris[stIdx++] = botA;
                sideTris[stIdx++] = topB;

                sideTris[stIdx++] = topB;
                sideTris[stIdx++] = botA;
                sideTris[stIdx++] = botB;
            }

            int totalTris = topTris.Length + bottomTris.Length + sideTriCount;
            int[] allTris = new int[totalTris];
            topTris.CopyTo(allTris, 0);
            bottomTris.CopyTo(allTris, topTris.Length);
            sideTris.CopyTo(allTris, topTris.Length + bottomTris.Length);

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = allTris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        public static Material CreateArrowMaterial(Color baseColor, Color emission)
        {
            // Try URP Lit first, then URP Unlit, then Standard / Unlit fallbacks
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Universal Render Pipeline/Unlit")
                            ?? Shader.Find("Standard")
                            ?? Shader.Find("Unlit/Color");

            var mat = new Material(shader);

            if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, baseColor);
            if (mat.HasProperty(ColorId)) mat.SetColor(ColorId, baseColor);

            // Enable Emission for high visibility in outdoor temple sunlight
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty(EmissionColorId)) mat.SetColor(EmissionColorId, emission * 1.25f);

            // Setup Transparent Alpha blending
            mat.SetFloat("_Surface", 1f); // Transparent
            mat.SetFloat("_Blend", 0f);   // Alpha
            mat.SetFloat("_ZWrite", 0f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");

            return mat;
        }

        /// <summary>
        /// Activates and snaps the arrow to an initial location.
        /// </summary>
        public void Spawn(Vector3 position, Quaternion rotation, Vector3 scale, float initialAlpha = 0f)
        {
            EnsureInitialized();
            IsInUse = true;
            gameObject.SetActive(true);

            _transform.position = TargetPosition = position;
            _transform.rotation = TargetRotation = rotation;
            _transform.localScale = TargetScale = scale;
            _currentAlpha = TargetAlpha = initialAlpha;

            ApplyAlpha(_currentAlpha);
        }

        /// <summary>
        /// Updates the arrow's animation, interpolation, and distance-based hover per frame.
        /// </summary>
        public void Step(float deltaTime)
        {
            if (!IsInUse) return;

            // Hover oscillation
            float hover = Mathf.Sin(Time.time * hoverFrequency + _hoverPhase) * hoverAmplitude;
            Vector3 targetWithHover = TargetPosition + Vector3.up * hover;

            // Smooth interpolation
            _transform.position = Vector3.Lerp(_transform.position, targetWithHover, deltaTime * positionLerpSpeed);
            _transform.rotation = Quaternion.Slerp(_transform.rotation, TargetRotation, deltaTime * rotationSlerpSpeed);
            _transform.localScale = Vector3.Lerp(_transform.localScale, TargetScale, deltaTime * scaleLerpSpeed);

            // Alpha transition
            _currentAlpha = Mathf.MoveTowards(_currentAlpha, TargetAlpha, deltaTime * alphaFadeSpeed);
            ApplyAlpha(_currentAlpha);

            // Hide if completely faded out and target is 0
            if (_currentAlpha <= 0.001f && TargetAlpha <= 0f)
            {
                gameObject.SetActive(false);
            }
            else if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
        }

        public void SetArrivedColor()
        {
            if (_material == null) return;
            if (_material.HasProperty(BaseColorId)) _material.SetColor(BaseColorId, arrivedColor);
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, arrivedColor);
            if (_material.HasProperty(EmissionColorId)) _material.SetColor(EmissionColorId, arrivedColor * 1.5f);
        }

        public void ResetColor()
        {
            if (_material == null) return;
            if (_material.HasProperty(BaseColorId)) _material.SetColor(BaseColorId, arrowColor);
            if (_material.HasProperty(ColorId)) _material.SetColor(ColorId, arrowColor);
            if (_material.HasProperty(EmissionColorId)) _material.SetColor(EmissionColorId, emissionColor * 1.25f);
        }

        void ApplyAlpha(float alpha)
        {
            if (_meshRenderer == null) return;

            _meshRenderer.GetPropertyBlock(_propBlock);
            Color c = arrowColor;
            c.a = alpha;
            _propBlock.SetColor(BaseColorId, c);
            _propBlock.SetColor(ColorId, c);
            _propBlock.SetColor(EmissionColorId, emissionColor * (alpha * 1.25f));
            _meshRenderer.SetPropertyBlock(_propBlock);
        }

        public void Recycle()
        {
            IsInUse = false;
            PathIndex = -1;
            TargetAlpha = 0f;
            _currentAlpha = 0f;
            ApplyAlpha(0f);
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
