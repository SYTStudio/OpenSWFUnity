using System.Collections.Generic;
using OpenSWFUnity.Runtime.Parser;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace OpenSWFUnity.Runtime.UI
{
    [DefaultExecutionOrder(-1000)]
    public sealed class SwfLibraryMenu : MonoBehaviour
    {
        private static readonly Color BackgroundTop = Hex("11172A");
        private static readonly Color BackgroundBottom = Hex("070A12");
        private static readonly Color SidebarColor = Hex("090D18");
        private static readonly Color CardColor = Hex("171E32");
        private static readonly Color CardSecondaryColor = Hex("11182A");
        private static readonly Color AccentColor = Hex("7C5CFF");
        private static readonly Color AccentBrightColor = Hex("9C87FF");
        private static readonly Color PrimaryTextColor = Hex("F5F7FF");
        private static readonly Color SecondaryTextColor = Hex("98A2BD");

        private SwfPlayer player;
        private Font uiFont;
        private GameObject libraryRoot;
        private GameObject nowPlayingHud;
        private Button playButton;
        private Text movieMetaText;
        private Text frameStatusText;
        private bool libraryVisible;
        private float nextStatusUpdate;

        private void Awake()
        {
            player = FindFirstObjectByType<SwfPlayer>();

            if (player == null)
            {
                enabled = false;
                return;
            }

            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            BuildInterface();
            ShowLibrary();
        }

        private void Update()
        {
            bool backPressed =
                (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);

            bool confirmPressed =
                (Keyboard.current != null &&
                 (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.spaceKey.wasPressedThisFrame)) ||
                (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame);

            if (!libraryVisible && backPressed)
            {
                ShowLibrary();
            }
            else if (libraryVisible && confirmPressed)
            {
                PlaySelectedMovie();
            }

            if (!libraryVisible && Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                player.Restart();
                player.Play();
            }

            if (Time.unscaledTime >= nextStatusUpdate)
            {
                nextStatusUpdate = Time.unscaledTime + 0.15f;
                RefreshStatus();
            }
        }

        public void ShowLibrary()
        {
            libraryVisible = true;
            player.Pause();
            player.inputEnabled = false;
            libraryRoot.SetActive(true);
            nowPlayingHud.SetActive(false);
            RefreshStatus();

            if (EventSystem.current != null && playButton != null)
            {
                EventSystem.current.SetSelectedGameObject(playButton.gameObject);
            }
        }

        public void PlaySelectedMovie()
        {
            libraryVisible = false;
            libraryRoot.SetActive(false);
            nowPlayingHud.SetActive(true);
            player.inputEnabled = true;
            player.Play();

            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        private void RefreshStatus()
        {
            if (player == null)
                return;

            SwfHeader header = player.LoadedHeader;

            if (movieMetaText != null)
            {
                movieMetaText.text = header == null
                    ? "LOADING SWF METADATA"
                    : Mathf.RoundToInt(header.StageWidth) + " × " +
                      Mathf.RoundToInt(header.StageHeight) + "   •   " +
                      header.FrameRate.ToString("0.#") + " FPS   •   SWF " + header.Version;
            }

            if (frameStatusText != null)
            {
                frameStatusText.text = player.HasLoadedMovie
                    ? "FRAME " + player.CurrentFrame.ToString("0000") + "   •   ESC  LIBRARY   •   R  RESTART"
                    : "PREPARING MOVIE";
            }
        }

        private void BuildInterface()
        {
            GameObject canvasObject = CreateUiObject("Console Library Canvas", transform);
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            libraryRoot = CreateUiObject("Library", canvasObject.transform);
            Stretch(libraryRoot.GetComponent<RectTransform>());

            Image background = AddImage(libraryRoot, Color.white);
            background.raycastTarget = false;
            UiVerticalGradient gradient = libraryRoot.AddComponent<UiVerticalGradient>();
            gradient.TopColor = BackgroundTop;
            gradient.BottomColor = BackgroundBottom;

            BuildDecorations(libraryRoot.transform);
            BuildSidebar(libraryRoot.transform);
            BuildLibraryContent(libraryRoot.transform);
            BuildNowPlayingHud(canvasObject.transform);
        }

        private void BuildDecorations(Transform parent)
        {
            GameObject glow = CreatePanel("Accent Glow", parent, new Color(0.33f, 0.19f, 0.85f, 0.12f));
            SetRect(glow.GetComponent<RectTransform>(), new Vector2(0.64f, 0.58f), new Vector2(1.03f, 1.1f));
            glow.transform.localRotation = Quaternion.Euler(0f, 0f, -14f);

            GameObject slashA = CreatePanel("Accent Slash A", parent, new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f));
            SetRect(slashA.GetComponent<RectTransform>(), new Vector2(0.72f, -0.08f), new Vector2(0.735f, 0.58f));
            slashA.transform.localRotation = Quaternion.Euler(0f, 0f, -18f);

            GameObject slashB = CreatePanel("Accent Slash B", parent, new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.08f));
            SetRect(slashB.GetComponent<RectTransform>(), new Vector2(0.78f, -0.08f), new Vector2(0.786f, 0.43f));
            slashB.transform.localRotation = Quaternion.Euler(0f, 0f, -18f);
        }

        private void BuildSidebar(Transform parent)
        {
            GameObject sidebar = CreatePanel("Sidebar", parent, SidebarColor);
            SetRect(sidebar.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0.165f, 1f));

            GameObject brandMark = CreatePanel("Brand Mark", sidebar.transform, AccentColor);
            SetRect(
                brandMark.GetComponent<RectTransform>(),
                new Vector2(0.1f, 0.86f),
                new Vector2(0.24f, 0.94f)
            );

            Text markText = CreateText("Brand Glyph", brandMark.transform, "O", 46, FontStyle.Bold, Color.white);
            Stretch(markText.rectTransform);
            markText.alignment = TextAnchor.MiddleCenter;

            Text brand = CreateText("Brand", sidebar.transform, "OPEN SWF", 29, FontStyle.Bold, PrimaryTextColor);
            SetRect(brand.rectTransform, new Vector2(0.3f, 0.865f), new Vector2(0.91f, 0.925f));
            brand.alignment = TextAnchor.MiddleLeft;

            Text edition = CreateText("Edition", sidebar.transform, "UNITY EDITION", 13, FontStyle.Bold, SecondaryTextColor);
            SetRect(edition.rectTransform, new Vector2(0.3f, 0.835f), new Vector2(0.91f, 0.875f));
            edition.alignment = TextAnchor.MiddleLeft;

            CreateNavigationItem(sidebar.transform, "LIBRARY", "▦", 0.68f, true);
            CreateNavigationItem(sidebar.transform, "RECENT", "◷", 0.60f, false);
            CreateNavigationItem(sidebar.transform, "FAVORITES", "◇", 0.52f, false);

            Text engineLabel = CreateText(
                "Engine Status",
                sidebar.transform,
                "ENGINE\nPRE-ALPHA  0.1",
                14,
                FontStyle.Bold,
                SecondaryTextColor
            );
            SetRect(engineLabel.rectTransform, new Vector2(0.1f, 0.055f), new Vector2(0.9f, 0.14f));
            engineLabel.alignment = TextAnchor.LowerLeft;
            engineLabel.lineSpacing = 1.25f;
        }

        private void CreateNavigationItem(Transform parent, string label, string icon, float top, bool selected)
        {
            GameObject item = CreatePanel(
                label + " Navigation",
                parent,
                selected ? new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.18f) : Color.clear
            );
            SetRect(item.GetComponent<RectTransform>(), new Vector2(0.065f, top - 0.065f), new Vector2(0.935f, top));

            if (selected)
            {
                GameObject indicator = CreatePanel("Selected", item.transform, AccentBrightColor);
                SetRect(indicator.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0.018f, 1f));
            }

            Text iconText = CreateText("Icon", item.transform, icon, 27, FontStyle.Normal, selected ? AccentBrightColor : SecondaryTextColor);
            SetRect(iconText.rectTransform, new Vector2(0.08f, 0f), new Vector2(0.25f, 1f));
            iconText.alignment = TextAnchor.MiddleCenter;

            Text itemText = CreateText("Label", item.transform, label, 17, FontStyle.Bold, selected ? PrimaryTextColor : SecondaryTextColor);
            SetRect(itemText.rectTransform, new Vector2(0.27f, 0f), new Vector2(0.95f, 1f));
            itemText.alignment = TextAnchor.MiddleLeft;
        }

        private void BuildLibraryContent(Transform parent)
        {
            Text eyebrow = CreateText("Eyebrow", parent, "YOUR COLLECTION", 15, FontStyle.Bold, AccentBrightColor);
            SetRect(eyebrow.rectTransform, new Vector2(0.215f, 0.865f), new Vector2(0.9f, 0.91f));
            eyebrow.alignment = TextAnchor.MiddleLeft;

            Text heading = CreateText("Heading", parent, "Flash Library", 52, FontStyle.Bold, PrimaryTextColor);
            SetRect(heading.rectTransform, new Vector2(0.215f, 0.79f), new Vector2(0.9f, 0.865f));
            heading.alignment = TextAnchor.MiddleLeft;

            Text subtitle = CreateText(
                "Subtitle",
                parent,
                "Classic SWF experiences, running natively inside Unity.",
                20,
                FontStyle.Normal,
                SecondaryTextColor
            );
            SetRect(subtitle.rectTransform, new Vector2(0.215f, 0.745f), new Vector2(0.9f, 0.79f));
            subtitle.alignment = TextAnchor.MiddleLeft;

            GameObject card = CreatePanel("Ruffle Demo Card", parent, CardColor);
            SetRect(card.GetComponent<RectTransform>(), new Vector2(0.215f, 0.245f), new Vector2(0.865f, 0.70f));
            Outline cardOutline = card.AddComponent<Outline>();
            cardOutline.effectColor = new Color(0.5f, 0.42f, 1f, 0.28f);
            cardOutline.effectDistance = new Vector2(2f, -2f);

            GameObject cover = CreatePanel("Cover", card.transform, CardSecondaryColor);
            SetRect(cover.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0.39f, 1f));
            cover.GetComponent<Image>().color = Color.white;
            UiVerticalGradient coverGradient = cover.AddComponent<UiVerticalGradient>();
            coverGradient.TopColor = Hex("5037B8");
            coverGradient.BottomColor = Hex("201750");

            GameObject coverLine = CreatePanel("Cover Accent", cover.transform, AccentBrightColor);
            SetRect(coverLine.GetComponent<RectTransform>(), new Vector2(0.09f, 0.79f), new Vector2(0.38f, 0.805f));

            Text swfLogo = CreateText("SWF Logo", cover.transform, "SWF", 82, FontStyle.Bold, Color.white);
            SetRect(swfLogo.rectTransform, new Vector2(0.085f, 0.31f), new Vector2(0.92f, 0.67f));
            swfLogo.alignment = TextAnchor.MiddleCenter;

            Text labLabel = CreateText("Lab Label", cover.transform, "COMPATIBILITY LAB", 14, FontStyle.Bold, new Color(1f, 1f, 1f, 0.72f));
            SetRect(labLabel.rectTransform, new Vector2(0.09f, 0.13f), new Vector2(0.91f, 0.22f));
            labLabel.alignment = TextAnchor.MiddleCenter;

            Text ready = CreateText("Ready", card.transform, "READY TO PLAY", 13, FontStyle.Bold, AccentBrightColor);
            SetRect(ready.rectTransform, new Vector2(0.44f, 0.76f), new Vector2(0.93f, 0.84f));
            ready.alignment = TextAnchor.MiddleLeft;

            Text title = CreateText("Movie Title", card.transform, GetMovieTitle(), 39, FontStyle.Bold, PrimaryTextColor);
            SetRect(title.rectTransform, new Vector2(0.44f, 0.59f), new Vector2(0.94f, 0.76f));
            title.alignment = TextAnchor.MiddleLeft;

            Text description = CreateText(
                "Movie Description",
                card.transform,
                "Current test movie for shapes, nested timelines, text, interaction and sound.",
                18,
                FontStyle.Normal,
                SecondaryTextColor
            );
            SetRect(description.rectTransform, new Vector2(0.44f, 0.42f), new Vector2(0.92f, 0.59f));
            description.alignment = TextAnchor.UpperLeft;
            description.horizontalOverflow = HorizontalWrapMode.Wrap;
            description.verticalOverflow = VerticalWrapMode.Truncate;

            movieMetaText = CreateText("Movie Metadata", card.transform, "LOADING SWF METADATA", 15, FontStyle.Bold, SecondaryTextColor);
            SetRect(movieMetaText.rectTransform, new Vector2(0.44f, 0.31f), new Vector2(0.94f, 0.41f));
            movieMetaText.alignment = TextAnchor.MiddleLeft;

            playButton = CreateButton(card.transform, "PLAY  ▶");
            SetRect(playButton.GetComponent<RectTransform>(), new Vector2(0.44f, 0.105f), new Vector2(0.69f, 0.27f));
            playButton.onClick.AddListener(PlaySelectedMovie);

            Text footer = CreateText(
                "Controls",
                parent,
                "A / ENTER   PLAY        B / ESC   LIBRARY        R   RESTART",
                14,
                FontStyle.Bold,
                SecondaryTextColor
            );
            SetRect(footer.rectTransform, new Vector2(0.215f, 0.105f), new Vector2(0.865f, 0.16f));
            footer.alignment = TextAnchor.MiddleRight;
        }

        private void BuildNowPlayingHud(Transform parent)
        {
            nowPlayingHud = CreatePanel("Now Playing HUD", parent, new Color(0.025f, 0.035f, 0.07f, 0.82f));
            SetRect(nowPlayingHud.GetComponent<RectTransform>(), new Vector2(0.63f, 0.925f), new Vector2(0.985f, 0.982f));
            nowPlayingHud.GetComponent<Image>().raycastTarget = false;

            frameStatusText = CreateText(
                "Frame Status",
                nowPlayingHud.transform,
                "PREPARING MOVIE",
                14,
                FontStyle.Bold,
                PrimaryTextColor
            );
            Stretch(frameStatusText.rectTransform, new Vector2(24f, 0f), new Vector2(-24f, 0f));
            frameStatusText.alignment = TextAnchor.MiddleRight;
            frameStatusText.raycastTarget = false;
            nowPlayingHud.SetActive(false);
        }

        private Button CreateButton(Transform parent, string label)
        {
            GameObject buttonObject = CreateUiObject("Play Button", parent);
            Image image = buttonObject.AddComponent<Image>();
            image.color = AccentColor;

            Button button = buttonObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = AccentColor;
            colors.highlightedColor = AccentBrightColor;
            colors.selectedColor = AccentBrightColor;
            colors.pressedColor = Hex("6043D8");
            colors.disabledColor = new Color(0.25f, 0.27f, 0.36f, 0.65f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            Text text = CreateText("Label", buttonObject.transform, label, 19, FontStyle.Bold, Color.white);
            Stretch(text.rectTransform);
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;

            return button;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;

            GameObject eventSystemObject = new GameObject("EventSystem");
            eventSystemObject.AddComponent<EventSystem>();
            InputSystemUIInputModule inputModule = eventSystemObject.AddComponent<InputSystemUIInputModule>();
            inputModule.AssignDefaultActions();
        }

        private string GetMovieTitle()
        {
            if (player == null || player.swfFile == null)
                return "Ruffle Demo";

            string title = player.swfFile.name.Replace('-', ' ').Replace('_', ' ');

            if (string.IsNullOrWhiteSpace(title))
                return "Ruffle Demo";

            return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title);
        }

        private GameObject CreateUiObject(string objectName, Transform parent)
        {
            GameObject result = new GameObject(objectName, typeof(RectTransform));
            result.layer = 5;
            result.transform.SetParent(parent, false);
            return result;
        }

        private GameObject CreatePanel(string objectName, Transform parent, Color color)
        {
            GameObject result = CreateUiObject(objectName, parent);
            Image image = result.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return result;
        }

        private Image AddImage(GameObject target, Color color)
        {
            Image image = target.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private Text CreateText(
            string objectName,
            Transform parent,
            string value,
            int size,
            FontStyle style,
            Color color
        )
        {
            GameObject textObject = CreateUiObject(objectName, parent);
            Text text = textObject.AddComponent<Text>();
            text.font = uiFont;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.supportRichText = false;
            text.raycastTarget = false;
            return text;
        }

        private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Stretch(RectTransform rect)
        {
            Stretch(rect, Vector2.zero, Vector2.zero);
        }

        private static void Stretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static Color Hex(string value)
        {
            ColorUtility.TryParseHtmlString("#" + value, out Color color);
            return color;
        }
    }

    internal static class SwfLibraryBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateLibraryForSwfScene()
        {
            SwfPlayer player = Object.FindFirstObjectByType<SwfPlayer>();

            if (player == null || !player.showLibraryOnStart)
                return;

            if (Object.FindFirstObjectByType<SwfLibraryMenu>() != null)
                return;

            GameObject menuObject = new GameObject("SWF Library Menu");
            menuObject.AddComponent<SwfLibraryMenu>();
        }
    }

    internal sealed class UiVerticalGradient : BaseMeshEffect
    {
        public Color TopColor = Color.white;
        public Color BottomColor = Color.black;

        public override void ModifyMesh(VertexHelper vertexHelper)
        {
            if (!IsActive() || vertexHelper.currentVertCount == 0)
                return;

            List<UIVertex> vertices = new List<UIVertex>();
            vertexHelper.GetUIVertexStream(vertices);

            float minY = float.MaxValue;
            float maxY = float.MinValue;

            for (int i = 0; i < vertices.Count; i++)
            {
                minY = Mathf.Min(minY, vertices[i].position.y);
                maxY = Mathf.Max(maxY, vertices[i].position.y);
            }

            float height = Mathf.Max(0.001f, maxY - minY);

            for (int i = 0; i < vertices.Count; i++)
            {
                UIVertex vertex = vertices[i];
                float t = Mathf.Clamp01((vertex.position.y - minY) / height);
                Color baseColor = vertex.color;
                vertex.color = (Color32)(baseColor * Color.Lerp(BottomColor, TopColor, t));
                vertices[i] = vertex;
            }

            vertexHelper.Clear();
            vertexHelper.AddUIVertexTriangleStream(vertices);
        }
    }
}
