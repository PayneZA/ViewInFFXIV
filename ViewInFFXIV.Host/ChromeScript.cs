namespace ViewInFFXIV.Host;

internal static class ChromeScript
{
    public const string HideAndVolume = """
        (() => {
          window.__viewinffxivVolume = window.__viewinffxivVolume ?? 1;
          window.__viewinffxivHideChrome = window.__viewinffxivHideChrome ?? true;

          const applyVolume = () => {
            const v = Number(window.__viewinffxivVolume);
            document.querySelectorAll('video, audio').forEach((el) => {
              try {
                el.volume = Math.min(1, Math.max(0, v));
                el.muted = v <= 0.001;
              } catch (e) {}
            });
          };

          const applyChrome = () => {
            let style = document.getElementById('viewinffxiv-chrome-hide');
            if (!window.__viewinffxivHideChrome) {
              if (style) style.remove();
              return;
            }
            if (!style) {
              style = document.createElement('style');
              style.id = 'viewinffxiv-chrome-hide';
              document.documentElement.appendChild(style);
            }
            style.textContent = `
              header, footer, nav, aside,
              [class*="chat" i], [class*="sidebar" i], [class*="toolbar" i],
              [class*="reaction" i], [data-testid*="chat" i] {
                display: none !important;
              }
              video {
                position: fixed !important;
                inset: 0 !important;
                width: 100vw !important;
                height: 100vh !important;
                object-fit: contain !important;
                background: #000 !important;
                z-index: 2147483646 !important;
              }
            `;
          };

          applyVolume();
          applyChrome();
          if (!window.__viewinffxivHooked) {
            window.__viewinffxivHooked = true;
            setInterval(() => { applyVolume(); applyChrome(); }, 750);
          }
          return true;
        })();
        """;
}
