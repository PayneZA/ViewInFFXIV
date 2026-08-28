namespace InView.Host;

internal static class ChromeScript
{
    public const string HideAndVolume = """
        (() => {
          window.__inviewVolume = window.__inviewVolume ?? 1;
          window.__inviewHideChrome = window.__inviewHideChrome ?? true;

          const applyVolume = () => {
            const v = Number(window.__inviewVolume);
            document.querySelectorAll('video, audio').forEach((el) => {
              try {
                el.volume = Math.min(1, Math.max(0, v));
                el.muted = v <= 0.001;
              } catch (e) {}
            });
          };

          const applyChrome = () => {
            let style = document.getElementById('inview-chrome-hide');
            if (!window.__inviewHideChrome) {
              if (style) style.remove();
              return;
            }
            if (!style) {
              style = document.createElement('style');
              style.id = 'inview-chrome-hide';
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
          if (!window.__inviewHooked) {
            window.__inviewHooked = true;
            setInterval(() => { applyVolume(); applyChrome(); }, 750);
          }
          return true;
        })();
        """;
}
