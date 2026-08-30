// UiKit.cs
// ---------------------------------------------------------------------------
// Small design system for the HARI-AR overlay: palette, generated sprites and
// widget builders.
//
// Everything is produced at runtime rather than imported, so the client still
// needs no authored assets — but the results are rounded cards, circular
// buttons and soft shadows instead of flat rectangles.
//
// Sprites are generated once and cached statically: a 256px rounded rect costs
// a few hundred microseconds to rasterise, and the HUD asks for the same three
// shapes repeatedly.
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HariAR.UI
{
    public static class UiKit
    {
        // ── Palette ──────────────────────────────────────────────────────────
        // Dark, desaturated surfaces so the camera feed stays readable behind
        // them, with a single saturated accent for the active instruction.

        public static readonly Color Surface      = new Color(0.07f, 0.08f, 0.10f, 0.88f);
        public static readonly Color SurfaceLight = new Color(1f, 1f, 1f, 0.12f);
        public static readonly Color Accent       = new Color(0.20f, 0.78f, 1.00f, 1f);
        public static readonly Color AccentDim    = new Color(0.20f, 0.78f, 1.00f, 0.25f);
        public static readonly Color Success      = new Color(0.24f, 0.85f, 0.45f, 1f);
        public static readonly Color Danger       = new Color(0.94f, 0.28f, 0.28f, 1f);
        public static readonly Color Warning      = new Color(1.00f, 0.72f, 0.20f, 1f);
        public static readonly Color TextPrimary  = new Color(1f, 1f, 1f, 0.98f);
        public static readonly Color TextSecondary= new Color(1f, 1f, 1f, 0.62f);

        // ── Fonts ────────────────────────────────────────────────────────────

        static Font _font;

        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                return _font;
            }
        }

        // ── Sprite cache ─────────────────────────────────────────────────────

        static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        /// <summary>
        /// A rounded rectangle, returned as a 9-sliced sprite so one texture
        /// serves every card size without distorting the corners.
        /// </summary>
        public static Sprite RoundedRect(int radius = 32)
        {
            string key = $"round_{radius}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            int size = radius * 2 + 4;
            var tex = NewTexture(size);
            var px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance from the rounded-rect boundary, anti-aliased.
                float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0f);
                float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0f);
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01(radius - dist + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }

            tex.SetPixels32(px);
            tex.Apply();

            var border = new Vector4(radius, radius, radius, radius);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f), 100f, 0,
                                       SpriteMeshType.FullRect, border);
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>A filled circle — the mic button and status dots.</summary>
        public static Sprite Circle(int size = 256)
        {
            string key = $"circle_{size}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var tex = NewTexture(size);
            var px = new Color32[size * size];
            float r = size * 0.5f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - r + 0.5f) * (x - r + 0.5f) +
                                     (y - r + 0.5f) * (y - r + 0.5f));
                float alpha = Mathf.Clamp01(r - d);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }

            tex.SetPixels32(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f));
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>A ring, used for the listening pulse around the mic.</summary>
        public static Sprite Ring(int size = 256, float thickness = 0.08f)
        {
            string key = $"ring_{size}_{thickness}";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var tex = NewTexture(size);
            var px = new Color32[size * size];
            float r = size * 0.5f;
            float inner = r * (1f - thickness * 2f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - r + 0.5f) * (x - r + 0.5f) +
                                     (y - r + 0.5f) * (y - r + 0.5f));
                float alpha = Mathf.Clamp01(r - d) * Mathf.Clamp01(d - inner);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
            }

            tex.SetPixels32(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f));
            Cache[key] = sprite;
            return sprite;
        }

        /// <summary>A microphone glyph, drawn rather than shipped as an icon.</summary>
        public static Sprite MicIcon(int size = 128)
        {
            const string key = "mic";
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var tex = NewTexture(size);
            var px = new Color32[size * size];
            var clear = new Color32(255, 255, 255, 0);
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            float cx = size * 0.5f;
            float capsuleW = size * 0.15f;          // half-width of the head
            float capsuleTop = size * 0.78f;
            float capsuleBottom = size * 0.42f;
            float arcRadius = size * 0.27f;
            float stemTop = size * 0.30f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x - cx;
                bool on = false;

                // Capsule head.
                if (y <= capsuleTop && y >= capsuleBottom && Mathf.Abs(dx) <= capsuleW)
                    on = true;
                float capTop = Mathf.Sqrt(Mathf.Max(0f, capsuleW * capsuleW - dx * dx));
                if (y > capsuleTop && y <= capsuleTop + capTop) on = true;
                if (y < capsuleBottom && y >= capsuleBottom - capTop) on = true;

                // Cradle arc.
                float d = Mathf.Sqrt(dx * dx + (y - capsuleBottom) * (y - capsuleBottom));
                if (y <= capsuleBottom && Mathf.Abs(d - arcRadius) <= size * 0.035f)
                    on = true;

                // Stem and base.
                if (Mathf.Abs(dx) <= size * 0.035f &&
                    y <= capsuleBottom - arcRadius && y >= stemTop) on = true;
                if (Mathf.Abs(dx) <= size * 0.17f &&
                    Mathf.Abs(y - stemTop) <= size * 0.035f) on = true;

                if (on) px[y * size + x] = new Color32(255, 255, 255, 255);
            }

            tex.SetPixels32(px);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                                       new Vector2(0.5f, 0.5f));
            Cache[key] = sprite;
            return sprite;
        }

        static Texture2D NewTexture(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        // ── Widget builders ──────────────────────────────────────────────────

        public static RectTransform Panel(string name, Transform parent, Color color,
                                          int cornerRadius = 32)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var image = go.GetComponent<Image>();
            image.sprite = RoundedRect(cornerRadius);
            image.type = Image.Type.Sliced;
            image.color = color;
            image.pixelsPerUnitMultiplier = 1f;

            return go.GetComponent<RectTransform>();
        }

        public static Text Label(string name, Transform parent, string content,
                                 int fontSize, Color color,
                                 TextAnchor anchor = TextAnchor.MiddleLeft,
                                 FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            var text = go.GetComponent<Text>();
            text.font = Font;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = anchor;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.supportRichText = false;

            return text;
        }

        public static Button CircleButton(string name, Transform parent, Color color,
                                          float diameter, Sprite icon = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image),
                                    typeof(Button));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(diameter, diameter);

            var image = go.GetComponent<Image>();
            image.sprite = Circle();
            image.color = color;

            if (icon != null)
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGo.transform.SetParent(go.transform, false);

                var iconRect = iconGo.GetComponent<RectTransform>();
                iconRect.sizeDelta = new Vector2(diameter * 0.45f, diameter * 0.45f);

                var iconImage = iconGo.GetComponent<Image>();
                iconImage.sprite = icon;
                iconImage.color = Color.white;
                iconImage.raycastTarget = false;
                iconImage.preserveAspect = true;
            }

            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.9f, 0.9f, 0.9f);
            colors.pressedColor = new Color(0.75f, 0.75f, 0.75f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            return button;
        }

        /// <summary>Anchor and size a RectTransform in one call.</summary>
        public static RectTransform Place(this RectTransform rect,
                                          Vector2 anchorMin, Vector2 anchorMax,
                                          Vector2 pivot, Vector2 position,
                                          Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        /// <summary>Stretch to fill the parent with optional padding.</summary>
        public static RectTransform Stretch(this RectTransform rect, float padding = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
            return rect;
        }
    }
}
