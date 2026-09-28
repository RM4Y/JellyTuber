namespace Jellyfin.Plugin.JellyTuber.Api;

/// <summary>Static HTML for the self-service management page (Space Grotesk theme).</summary>
internal static class PageHtml
{
    public const string Html = """
<!DOCTYPE html>
<html lang="fr">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover" />
<title>JellyTuber</title>
<link rel="icon" type="image/svg+xml" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 280 280'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='%238927be'/><stop offset='1' stop-color='%23a793f4'/></linearGradient></defs><rect width='280' height='280' rx='64' fill='url(%23g)'/><g transform='rotate(-16 140 145) translate(140 145) scale(1.25) translate(-140 -145)'><g stroke='%23ffffff' stroke-width='9' stroke-linecap='round' fill='none'><path d='M140.0,106.0 C137.3,106.7 129.3,108.4 124.0,110.1 C118.7,111.7 113.3,115.2 108.0,115.8 C102.7,116.4 97.3,115.4 92.0,113.8 C86.7,112.1 78.7,107.3 76.0,106.0'/><path d='M140.0,134.0 C138.0,134.8 132.0,137.7 128.0,139.0 C124.0,140.2 120.0,142.1 116.0,141.7 C112.0,141.2 108.0,138.2 104.0,136.5 C100.0,134.7 96.0,131.8 92.0,131.3 C88.0,130.9 82.0,133.6 80.0,134.0'/><path d='M140.0,158.0 C138.0,158.3 132.0,158.6 128.0,159.9 C124.0,161.2 120.0,164.5 116.0,165.8 C112.0,167.0 108.0,168.0 104.0,167.5 C100.0,167.0 96.0,164.3 92.0,162.7 C88.0,161.1 82.0,158.8 80.0,158.0'/><path d='M140.0,186.0 C137.3,186.8 129.3,189.2 124.0,190.8 C118.7,192.5 113.3,195.5 108.0,196.0 C102.7,196.5 97.3,195.4 92.0,193.8 C86.7,192.1 78.7,187.3 76.0,186.0'/></g><path d='M 140 90 L 210 145 L 140 200 Z' fill='%23ffffff' stroke='%23ffffff' stroke-width='22' stroke-linejoin='round'/></g></svg>" />
<link rel="apple-touch-icon" href="/JellyTuber/apple-touch-icon.png" />
<link rel="manifest" href="/JellyTuber/manifest.webmanifest" />
<meta name="apple-mobile-web-app-capable" content="yes" />
<meta name="apple-mobile-web-app-title" content="JellyTuber" />
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
<meta name="theme-color" content="#0c1116" />
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;600;700&display=swap" rel="stylesheet">
<style>
  :root {
    --bg:#0c1116; --bg-elev:#141b22; --bg-elev2:#1b242d; --border:#243039;
    --text:#e9eff2; --text-dim:#8aa0ab; --accent:#9b4fd3; --accent-2:#a793f4;
    --on-accent:#ffffff; --glow:rgba(155,79,211,0.28); --card-shadow:0 8px 30px rgba(0,0,0,0.35);
  }
  :root.light {
    --bg:#eef3f4; --bg-elev:#ffffff; --bg-elev2:#f2f7f8; --border:#dbe5e8;
    --text:#13232a; --text-dim:#5b7079; --accent:#861cb4; --accent-2:#8c63dd;
    --on-accent:#ffffff; --glow:rgba(134,28,180,0.16); --card-shadow:0 8px 30px rgba(20,50,60,0.10);
  }
  * { box-sizing: border-box; }
  html, body { margin:0; padding:0; }
  /* No pinch/double-tap zoom: pan-x pan-y is honoured by iOS Safari, which ignores user-scalable=no. */
  html { touch-action:pan-x pan-y; -webkit-text-size-adjust:100%; text-size-adjust:100%; }
  body {
    font-family:'Space Grotesk', system-ui, sans-serif; background:var(--bg); color:var(--text);
    min-height:100vh; min-height:100dvh; transition:background .3s, color .3s;
    -webkit-tap-highlight-color:transparent;
  }
  button { touch-action:manipulation; }
  .glow { position:fixed; inset:0; pointer-events:none; overflow:hidden; z-index:0; }
  .glow div { position:absolute; top:-200px; left:50%; transform:translateX(-50%);
    width:760px; height:520px; background:radial-gradient(ellipse at center, var(--glow), transparent 70%); filter:blur(20px); }
  .wrap { position:relative; z-index:1; }
  .hidden { display:none !important; }

  input { font-family:inherit; }
  input::placeholder { color:var(--text-dim); opacity:.8; }
  input:focus { outline:none; border-color:var(--accent) !important; box-shadow:0 0 0 3px var(--glow); }
  .field {
    width:100%; padding:13px 15px; border-radius:11px; border:1px solid var(--border);
    background:var(--bg-elev2); color:var(--text); font-size:14.5px;
  }
  .btn-accent {
    border:none; border-radius:11px; background:linear-gradient(135deg,var(--accent),var(--accent-2));
    color:var(--on-accent); font-family:inherit; font-weight:700; cursor:pointer;
    box-shadow:0 4px 14px var(--glow); transition:transform .12s, box-shadow .2s;
  }
  @media (hover:hover) { .btn-accent:hover { transform:translateY(-1px); box-shadow:0 10px 26px var(--glow); } }
  .btn-ghost {
    border:1px solid var(--border); border-radius:11px; background:var(--bg-elev);
    color:var(--text); font-family:inherit; font-weight:600; cursor:pointer; transition:border-color .15s, color .15s;
  }
  .btn-ghost:hover { border-color:var(--accent); color:var(--accent); }

  .logo { border-radius:13px; background:linear-gradient(135deg,var(--accent),var(--accent-2));
    display:flex; align-items:center; justify-content:center; box-shadow:0 6px 20px var(--glow); flex:none; }
  .brandName { font-weight:700; letter-spacing:1.5px; color:var(--text); }
  .brandName b { color:var(--accent); font-weight:700; }

  .panel { background:var(--bg-elev); border:1px solid var(--border); border-radius:18px;
    padding:22px; box-shadow:var(--card-shadow); }
  .label { display:block; font-size:12px; font-weight:600; letter-spacing:.4px;
    color:var(--text-dim); margin-bottom:7px; text-transform:uppercase; }

  .avatar { width:38px; height:38px; border-radius:50%; flex:none; display:flex;
    align-items:center; justify-content:center; font-weight:700; font-size:15px; }
  .rowline { display:flex; align-items:center; gap:13px; }
  .name { flex:1; min-width:0; font-weight:600; color:var(--text);
    white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }

  .pill { font-size:12.5px; font-weight:600; padding:4px 11px; border-radius:999px;
    background:var(--bg-elev2); border:1px solid var(--border); color:var(--text-dim); }
  .tab { flex:1; padding:11px 14px; border-radius:12px; border:1px solid var(--border);
    background:var(--bg-elev); color:var(--text-dim); font-family:inherit; font-weight:600;
    font-size:14px; cursor:pointer; transition:border-color .15s, color .15s, background .15s; }
  .tab:hover { border-color:var(--accent); color:var(--accent); }
  .tab.active { background:var(--glow); border-color:var(--accent); color:var(--accent); }
  .shorts { display:flex; align-items:center; gap:7px; padding:7px 13px; border-radius:999px;
    font-family:inherit; font-size:12.5px; font-weight:600; cursor:pointer; border:1px solid var(--border);
    background:transparent; color:var(--text-dim); flex:none; }
  .shorts.on { border-color:var(--accent); background:var(--glow); color:var(--accent); }
  .dot { width:8px; height:8px; border-radius:50%; background:var(--text-dim); }
  .shorts.on .dot { background:var(--accent); box-shadow:0 0 8px var(--accent); }
  .maxvid { display:flex; align-items:center; gap:6px; padding:4px 5px 4px 12px; border-radius:999px;
    border:1px solid var(--border); background:transparent; flex:none; }
  .maxvid span { font-size:12.5px; font-weight:600; color:var(--text-dim); white-space:nowrap; }
  .maxvid button { width:26px; height:26px; border-radius:50%; border:1px solid var(--border);
    background:var(--bg-elev2); color:var(--text); font-family:inherit; font-size:15px; font-weight:700;
    line-height:1; cursor:pointer; padding:0; display:flex; align-items:center; justify-content:center; }
  .maxvid button:hover:not(:disabled) { border-color:var(--accent); color:var(--accent); }
  .maxvid button:disabled { opacity:.35; cursor:default; }
  .maxvid b { font-size:13px; font-weight:700; color:var(--accent); min-width:1.6em; text-align:center; }
  .vthumb { width:96px; height:54px; border-radius:8px; object-fit:cover; flex:none; background:var(--bg-elev);
    border:1px solid var(--border); display:block; }
  .vtitle { font-size:14px; font-weight:600; color:var(--text); line-height:1.3;
    display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
  .vmeta { font-size:12.5px; color:var(--text-dim); margin-top:3px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .remove { padding:8px 14px; border-radius:10px; border:1px solid rgba(224,115,138,.45); background:rgba(224,115,138,.12);
    color:#e0738a; font-family:inherit; font-size:13px; font-weight:600; cursor:pointer; flex:none; }
  .remove:hover { border-color:#e0738a; background:#e0738a; color:#fff; }
  .remove:disabled { opacity:.55; cursor:default; }
  :root.light .remove { color:#c9405e; border-color:rgba(201,64,94,.4); background:rgba(201,64,94,.08); }
  :root.light .remove:hover { background:#c9405e; border-color:#c9405e; color:#fff; }

  .toggle { position:fixed; top:calc(20px + env(safe-area-inset-top)); right:calc(22px + env(safe-area-inset-right)); z-index:20; display:flex; align-items:center; gap:9px;
    padding:8px 14px; border-radius:999px; border:1px solid var(--border); background:var(--bg-elev);
    color:var(--text); font-family:inherit; font-size:13px; font-weight:500; cursor:pointer; box-shadow:var(--card-shadow); }
  .toggle:hover { border-color:var(--accent); }

  .overlay { position:fixed; inset:0; background:rgba(0,0,0,.6); display:flex; align-items:center;
    justify-content:center; z-index:30; backdrop-filter:blur(2px); }
  .modal { background:var(--bg-elev); border:1px solid var(--border); border-radius:18px; padding:26px;
    max-width:380px; text-align:center; box-shadow:0 10px 34px rgba(0,0,0,.5); margin:0 1rem; }
  .toast { position:fixed; bottom:calc(22px + env(safe-area-inset-bottom)); left:50%; max-width:calc(100% - 32px); text-align:center; transform:translateX(-50%); z-index:40;
    background:linear-gradient(135deg,var(--accent),var(--accent-2)); color:var(--on-accent);
    padding:12px 20px; border-radius:12px; font-weight:600; font-size:13.5px; box-shadow:0 8px 22px var(--glow);
    animation:fade .3s ease; }
  @keyframes fade { from { opacity:0; transform:translate(-50%,8px); } to { opacity:1; transform:translate(-50%,0); } }
  @keyframes spin { to { transform:rotate(360deg); } }
  .spin { animation:spin .9s linear infinite; }

  /* ---- Motion ---- */
  /* Press feedback: actions squash a little under the finger and spring back. */
  .btn-accent, .btn-ghost, .tab, .shorts, .remove, .maxvid button, .toggle, .vg, .chancard, .linkcard, .seg button {
    transition:transform .22s cubic-bezier(.34,1.56,.64,1), background .2s, border-color .2s,
      color .2s, box-shadow .2s, opacity .2s; }
  .btn-accent:active:not(:disabled), .btn-ghost:active, .tab:active, .shorts:active, .remove:active,
  .maxvid button:active:not(:disabled), .toggle:active { transform:scale(.94); transition-duration:.08s; }
  .vg:active { transform:scale(.97); transition-duration:.08s; }
  .seg button:active { transform:scale(.94); transition-duration:.08s; }
  .chancard:active:not(:has(button:active)), .linkcard:active:not(:has(button:active)) { transform:scale(.985); transition-duration:.08s; }
  .toggle svg { transition:transform .5s cubic-bezier(.34,1.56,.64,1); }
  .toggle:active svg { transform:rotate(-90deg) scale(.85); }
  .backBtn svg { transition:transform .2s; }
  @media (hover:hover) { .backBtn:hover svg { transform:translateX(-3px); } }
  .tab.active { box-shadow:0 4px 16px var(--glow); }

  /* Items appear with a short staggered rise (--i = position in the list). */
  .enter { animation:rise .38s cubic-bezier(.2,.8,.3,1) both; animation-delay:calc(var(--i, 0) * 40ms); }
  @keyframes rise { from { opacity:0; transform:translateY(10px) scale(.98); } to { opacity:1; transform:none; } }
  /* ... and fold away when removed. */
  .leaving { overflow:hidden; opacity:0; transform:scale(.96) translateX(12px);
    max-height:0 !important; padding-top:0 !important; padding-bottom:0 !important; margin-top:-10px; border-width:0;
    transition:opacity .2s, transform .25s, max-height .3s .1s, padding .3s .1s, margin .3s .1s, border-width .3s .1s !important; }
  .pop { animation:pop .4s cubic-bezier(.34,1.56,.64,1); }
  @keyframes pop { 0% { transform:scale(1); } 40% { transform:scale(1.08); } 100% { transform:scale(1); } }
  .bump { animation:bump .3s cubic-bezier(.34,1.56,.64,1); }
  @keyframes bump { 0% { transform:scale(1.45); } 100% { transform:scale(1); } }
  .shorts .dot { transition:background .2s, box-shadow .2s; }
  .shorts.flip .dot { animation:dotpop .35s cubic-bezier(.34,1.56,.64,1); }
  @keyframes dotpop { 0% { transform:scale(1); } 45% { transform:scale(1.9); } 100% { transform:scale(1); } }
  .overlay:not(.hidden) { animation:fadeIn .2s ease both; }
  .overlay:not(.hidden) .modal { animation:modalIn .32s cubic-bezier(.34,1.56,.64,1) both; }
  @keyframes fadeIn { from { opacity:0; } }
  @keyframes modalIn { from { opacity:0; transform:translateY(12px) scale(.94); } }
  .toast.out { animation:fadeOut .25s ease forwards; }
  @keyframes fadeOut { to { opacity:0; transform:translate(-50%,8px); } }
  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation-duration:.01ms !important; animation-delay:0s !important;
      animation-iteration-count:1 !important; transition-duration:.01ms !important; }
  }

  input[type=search]::-webkit-search-cancel-button { display:none; }
  /* Scrolling placeholder: a real placeholder can't move, so an overlay
     stands in for it while the field is empty (placeholder=" " keeps
     :placeholder-shown working), and slides when it doesn't fit. */
  .phWrap { position:relative; }
  .phScroll { position:absolute; left:16px; right:16px; top:0; bottom:0; display:flex; align-items:center;
    overflow:hidden; pointer-events:none; color:var(--text-dim); opacity:.8; font-size:14.5px; white-space:nowrap; }
  .phWrap input:not(:placeholder-shown) + .phScroll { display:none; }
  .phScroll span { display:inline-block; }
  .phScroll.run span { animation:phslide var(--dur, 8s) ease-in-out infinite; }
  @keyframes phslide { 0%, 18% { transform:translateX(0); } 72%, 88% { transform:translateX(var(--d)); } 100% { transform:translateX(0); } }
  .chev { flex:none; color:var(--text-dim); }
  .chancard, .linkcard { cursor:pointer; }
  @media (hover:hover) { .chancard:hover, .linkcard:hover { border-color:var(--accent); }
    .chancard:hover .chev, .linkcard:hover .chev { color:var(--accent); } }
  .openIn { display:flex; align-items:center; justify-content:space-between; gap:12px; flex-wrap:wrap;
    padding:12px 14px 12px 16px !important; margin-bottom:22px; }
  .openIn > span { font-size:13.5px; font-weight:600; }
  .seg { display:flex; gap:4px; padding:3px; border-radius:11px; background:var(--bg-elev2); border:1px solid var(--border); }
  .seg button { border:none; background:transparent; color:var(--text-dim); font-family:inherit; font-size:13px;
    font-weight:600; padding:7px 13px; border-radius:8px; cursor:pointer; }
  .seg button.on { background:linear-gradient(135deg,var(--accent),var(--accent-2)); color:var(--on-accent); box-shadow:0 3px 10px var(--glow); }

  /* Channel page */
  .backBtn { display:inline-flex; align-items:center; gap:7px; padding:9px 14px 9px 11px; font-size:13.5px; margin-bottom:18px; }
  .chead { display:flex; align-items:center; gap:16px; margin-bottom:22px; }
  .chead .avatar { width:62px; height:62px; font-size:24px; }
  .chead h1 { margin:0 0 5px; font-size:24px; font-weight:700; letter-spacing:-.4px; line-height:1.2;
    overflow:hidden; text-overflow:ellipsis; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; }
  .chead p { margin:0; font-size:13.5px; color:var(--text-dim); }
  .ytLink { flex:none; display:inline-flex; align-items:center; gap:7px; padding:9px 14px; font-size:13px; text-decoration:none; }
  .vgrid { display:grid; grid-template-columns:repeat(auto-fill, minmax(170px, 1fr)); gap:14px; }
  .vg { display:block; text-decoration:none; color:inherit; border-radius:14px; overflow:hidden; min-width:0;
    background:var(--bg-elev); border:1px solid var(--border); transition:border-color .15s, transform .15s; }
  @media (hover:hover) { .vg:hover { border-color:var(--accent); transform:translateY(-2px); } }
  .vg-thumb { position:relative; aspect-ratio:16/9; background:var(--bg-elev2); }
  .vg-thumb img { width:100%; height:100%; object-fit:cover; display:block; }
  .vg-dur { position:absolute; right:6px; bottom:6px; background:rgba(0,0,0,.78); color:#fff;
    font-size:11.5px; font-weight:600; padding:2px 6px; border-radius:6px; }
  .vg-lib { position:absolute; left:6px; top:6px; background:linear-gradient(135deg,var(--accent),var(--accent-2));
    color:#fff; font-size:10.5px; font-weight:700; padding:3px 7px; border-radius:6px; letter-spacing:.2px; }
  .vg-body { padding:10px 11px 12px; }
  .vg-title { font-size:13.5px; font-weight:600; line-height:1.3; display:-webkit-box; -webkit-line-clamp:2;
    -webkit-box-orient:vertical; overflow:hidden; }
  .vg-meta { font-size:12px; color:var(--text-dim); margin-top:5px; }
  .sk { background:linear-gradient(90deg, var(--bg-elev2) 30%, var(--border) 50%, var(--bg-elev2) 70%);
    background-size:200% 100%; animation:sk 1.3s linear infinite; border-radius:6px; }
  @keyframes sk { from { background-position:100% 0; } to { background-position:-100% 0; } }
  #app { padding:calc(30px + env(safe-area-inset-top)) calc(22px + env(safe-area-inset-right))
    calc(70px + env(safe-area-inset-bottom)) calc(22px + env(safe-area-inset-left)); }
  #login { min-height:100vh; min-height:100dvh; }

  @media (max-width:560px) {
    #app { padding:calc(18px + env(safe-area-inset-top)) calc(16px + env(safe-area-inset-right))
      calc(40px + env(safe-area-inset-bottom)) calc(16px + env(safe-area-inset-left)); }
    #login { padding:28px 16px !important; }
    #login .panel { padding:26px 20px !important; }
    .brandRow { margin-bottom:18px !important; min-height:34px; }
    /* Theme toggle: icon-only, scrolls away with the page instead of floating over the cards. */
    .toggle { position:absolute; top:calc(18px + env(safe-area-inset-top)); right:calc(16px + env(safe-area-inset-right));
      width:34px; height:34px; padding:0; justify-content:center; box-shadow:none; }
    .toggle span { display:none; }
    .headRow { align-items:flex-start !important; margin-bottom:18px !important; gap:12px !important; }
    .headRow h1 { font-size:23px !important; letter-spacing:-.3px !important; }
    .headRow p { font-size:13px !important; }
    .headRow .btn-ghost { padding:8px 12px !important; font-size:12.5px !important; }
    .tabs { margin-bottom:16px !important; }
    .panel { padding:16px; border-radius:16px; }
    .field { font-size:16px; padding:12px 13px; } /* >=16px: iOS doesn't zoom on focus */
    .phScroll { left:14px; right:14px; font-size:16px; }
    /* Nested scroll boxes are awkward on touch: let results flow with the page. */
    #results, #vresults { max-height:none !important; overflow:visible !important; }
    .rowline { gap:11px; }
    /* Channel card: avatar + name + "Retirer" on the first line, settings on the second. */
    .chancard { flex-wrap:wrap; row-gap:12px; padding:14px !important; }
    .chancard .name { flex:1 1 calc(100% - 170px); }
    .chancard .maxvid, .chancard .shorts { order:1; }
    .maxvid button { width:30px; height:30px; }
    .shorts, .remove { padding:8px 12px; }
    .vcard { padding:12px 14px !important; }
    .vcard .name { white-space:normal; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical;
      font-size:14px !important; line-height:1.3; }
    .vthumb { width:80px; height:45px; }
    .vtitle { font-size:13.5px; }
    .syncRow { flex-wrap:wrap; gap:10px !important; }
    .backBtn { margin-bottom:14px; }
    .chead { gap:12px; margin-bottom:16px; }
    .chead .avatar { width:50px; height:50px; font-size:20px; }
    .chead h1 { font-size:20px; margin-bottom:3px; }
    .chead p { font-size:12.5px; }
    .ytLink { padding:9px; }
    .ytLink span { display:none; }
    .vgrid { grid-template-columns:repeat(2, minmax(0, 1fr)); gap:10px; }
    .vg { border-radius:12px; }
    .vg-body { padding:8px 9px 10px; }
    .vg-title { font-size:13px; }
    .vg-meta { font-size:11.5px; margin-top:4px; }
    .syncRow .btn-accent { flex:1 1 100%; justify-content:center; }
  }
  @media (max-width:360px) {
    .shorts { font-size:12px; padding:8px 10px; }
    .maxvid span { display:none; }
    .maxvid { padding-left:5px; }
  }
</style>
</head>
<body>

<div class="glow"><div></div></div>

<button class="toggle hidden" id="themeBtn">
  <svg id="themeIcon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"></svg>
  <span id="themeLabel"></span>
</button>

<div class="wrap">

  <!-- LOGIN -->
  <div id="login" style="display:flex; flex-direction:column; align-items:center; justify-content:center; padding:40px 20px;">
    <div style="display:flex; align-items:center; gap:13px; margin-bottom:34px;">
      <div class="logo" style="width:46px; height:46px;">
        <svg width="36" height="36" viewBox="0 0 280 280" style="overflow:visible;">
          <g transform="rotate(-16 140 145) translate(140 145) scale(1.25) translate(-140 -145)">
            <g stroke="#fff" stroke-width="9" stroke-linecap="round" fill="none">
              <path d="M140.0,106.0 C137.3,106.7 129.3,108.4 124.0,110.1 C118.7,111.7 113.3,115.2 108.0,115.8 C102.7,116.4 97.3,115.4 92.0,113.8 C86.7,112.1 78.7,107.3 76.0,106.0"></path>
              <path d="M140.0,134.0 C138.0,134.8 132.0,137.7 128.0,139.0 C124.0,140.2 120.0,142.1 116.0,141.7 C112.0,141.2 108.0,138.2 104.0,136.5 C100.0,134.7 96.0,131.8 92.0,131.3 C88.0,130.9 82.0,133.6 80.0,134.0"></path>
              <path d="M140.0,158.0 C138.0,158.3 132.0,158.6 128.0,159.9 C124.0,161.2 120.0,164.5 116.0,165.8 C112.0,167.0 108.0,168.0 104.0,167.5 C100.0,167.0 96.0,164.3 92.0,162.7 C88.0,161.1 82.0,158.8 80.0,158.0"></path>
              <path d="M140.0,186.0 C137.3,186.8 129.3,189.2 124.0,190.8 C118.7,192.5 113.3,195.5 108.0,196.0 C102.7,196.5 97.3,195.4 92.0,193.8 C86.7,192.1 78.7,187.3 76.0,186.0"></path>
            </g>
            <path d="M 140 90 L 210 145 L 140 200 Z" fill="#fff" stroke="#fff" stroke-width="22" stroke-linejoin="round"></path>
          </g>
        </svg>
      </div>
      <div style="display:flex; flex-direction:column; line-height:1.05;">
        <span class="brandName" style="font-size:19px;">JELLY <b>TUBER</b></span>
        <span style="font-size:10.5px; font-weight:500; letter-spacing:2.5px; color:var(--text-dim);">PLUGIN JELLYFIN</span>
      </div>
    </div>

    <div class="panel" style="width:100%; max-width:392px; border-radius:20px; padding:32px 30px;">
      <h1 style="margin:0 0 5px; font-size:25px; font-weight:700; letter-spacing:-.3px;">Connexion</h1>
      <p style="margin:0 0 24px; font-size:14px; color:var(--text-dim);">Utilisez votre compte Jellyfin.</p>

      <label class="label">Nom d'utilisateur</label>
      <input id="u" class="field" style="margin-bottom:17px" placeholder="Votre identifiant" />

      <label class="label">Mot de passe</label>
      <input id="p" type="password" class="field" style="margin-bottom:24px" placeholder="••••••••" />

      <button id="loginBtn" class="btn-accent" style="width:100%; padding:14px; font-size:15px;">Se connecter</button>
      <p id="loginErr" style="margin:14px 0 0; font-size:13px; color:#e0738a;"></p>
    </div>
    <p style="margin:26px 0 0; font-size:12.5px; color:var(--text-dim);">Synchronisez vos chaînes YouTube dans Jellyfin, sans les Shorts.</p>
  </div>

  <!-- APP -->
  <div id="app" class="hidden" style="max-width:760px; margin:0 auto;">
    <div class="brandRow" style="display:flex; align-items:center; gap:11px; margin-bottom:26px;">
      <div class="logo" style="width:34px; height:34px; border-radius:10px;">
        <svg width="26" height="26" viewBox="0 0 280 280" style="overflow:visible;">
          <g transform="rotate(-16 140 145) translate(140 145) scale(1.25) translate(-140 -145)">
            <g stroke="#fff" stroke-width="9" stroke-linecap="round" fill="none">
              <path d="M140.0,106.0 C137.3,106.7 129.3,108.4 124.0,110.1 C118.7,111.7 113.3,115.2 108.0,115.8 C102.7,116.4 97.3,115.4 92.0,113.8 C86.7,112.1 78.7,107.3 76.0,106.0"></path>
              <path d="M140.0,134.0 C138.0,134.8 132.0,137.7 128.0,139.0 C124.0,140.2 120.0,142.1 116.0,141.7 C112.0,141.2 108.0,138.2 104.0,136.5 C100.0,134.7 96.0,131.8 92.0,131.3 C88.0,130.9 82.0,133.6 80.0,134.0"></path>
              <path d="M140.0,158.0 C138.0,158.3 132.0,158.6 128.0,159.9 C124.0,161.2 120.0,164.5 116.0,165.8 C112.0,167.0 108.0,168.0 104.0,167.5 C100.0,167.0 96.0,164.3 92.0,162.7 C88.0,161.1 82.0,158.8 80.0,158.0"></path>
              <path d="M140.0,186.0 C137.3,186.8 129.3,189.2 124.0,190.8 C118.7,192.5 113.3,195.5 108.0,196.0 C102.7,196.5 97.3,195.4 92.0,193.8 C86.7,192.1 78.7,187.3 76.0,186.0"></path>
            </g>
            <path d="M 140 90 L 210 145 L 140 200 Z" fill="#fff" stroke="#fff" stroke-width="22" stroke-linejoin="round"></path>
          </g>
        </svg>
      </div>
      <span class="brandName" style="font-size:14px;">JELLY <b>TUBER</b></span>
    </div>

    <div id="mainView">
    <div class="headRow" style="display:flex; align-items:flex-end; justify-content:space-between; gap:16px; margin-bottom:24px;">
      <div>
        <h1 style="margin:0 0 4px; font-size:31px; font-weight:700; letter-spacing:-.6px;">Ma bibliothèque YouTube</h1>
        <p style="margin:0; font-size:14px; color:var(--text-dim);">Gérez vos chaînes et vos vidéos synchronisées dans Jellyfin.</p>
      </div>
      <button id="logoutBtn" class="btn-ghost" style="flex-shrink:0; padding:10px 16px; font-size:13.5px;">Déconnexion</button>
    </div>

    <div class="tabs" style="display:flex; gap:8px; margin-bottom:22px;">
      <button id="tabBtnChaine" class="tab active" type="button">Chaîne</button>
      <button id="tabBtnVideo" class="tab" type="button">Vidéo</button>
    </div>

    <!-- TAB: CHAÎNE (fonctionnalité existante, inchangée) -->
    <div id="tabChaine">
    <div class="panel" style="margin-bottom:26px;">
      <div style="display:flex; align-items:center; gap:8px; margin-bottom:15px;">
        <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.5" y2="16.5"/></svg>
        <h2 style="margin:0; font-size:16px; font-weight:600;">Ajouter une chaîne</h2>
      </div>
      <input id="q" type="search" enterkeyhint="search" autocomplete="off" class="field" placeholder="Rechercher une chaîne YouTube…" />
      <div id="results" style="display:flex; flex-direction:column; gap:8px; margin-top:18px; max-height:430px; overflow-y:auto;"></div>
    </div>

    <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:13px;">
      <h2 style="margin:0; font-size:17px; font-weight:600;">Mes chaînes</h2>
      <span id="count" class="pill">0 chaîne</span>
    </div>
    <div id="mine" style="display:flex; flex-direction:column; gap:10px; margin-bottom:28px;"></div>
    </div><!-- /tabChaine -->

    <!-- TAB: VIDÉO -->
    <div id="tabVideo" class="hidden">
      <div class="panel" style="margin-bottom:26px;">
        <div style="display:flex; align-items:center; gap:8px; margin-bottom:15px;">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="16" rx="3"/><polygon points="10,9 16,12 10,15" fill="var(--accent)" stroke="none"/></svg>
          <h2 style="margin:0; font-size:16px; font-weight:600;">Ajouter une vidéo</h2>
        </div>
        <div class="phWrap">
          <input id="vq" type="search" enterkeyhint="search" autocomplete="off" class="field" placeholder=" " aria-label="Collez un lien ou tapez le nom d'une vidéo" />
          <div id="vqPh" class="phScroll" aria-hidden="true"><span>Collez un lien ou tapez le nom d'une vidéo…</span></div>
        </div>
        <p id="vmsg" style="margin:11px 0 0; font-size:13px; color:var(--text-dim); min-height:1px;"></p>
        <div id="vresults" style="display:flex; flex-direction:column; gap:8px; margin-top:8px; max-height:520px; overflow-y:auto;"></div>
      </div>

      <div class="panel" style="margin-bottom:26px;">
        <div style="display:flex; align-items:center; gap:8px; margin-bottom:16px;">
          <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12v7a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7"/><polyline points="16 6 12 2 8 6"/><line x1="12" y1="2" x2="12" y2="15"/></svg>
          <h2 style="margin:0; font-size:16px; font-weight:600;">Partage depuis l'iPhone</h2>
        </div>
        <div style="display:flex; flex-direction:column; align-items:center; gap:10px;">
          <button id="copyShareBtn" class="btn-accent" disabled style="display:flex; align-items:center; gap:9px; padding:13px 22px; font-size:14px; max-width:100%;">
            <svg style="flex:none;" width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
            <span id="shareLinkText" style="min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">Chargement…</span>
          </button>
          <button id="regenShareBtn" class="btn-ghost" style="padding:8px 14px; font-size:12.5px;">Générer un nouveau code</button>
        </div>
      </div>

      <div style="display:flex; align-items:center; justify-content:space-between; margin-bottom:13px;">
        <h2 style="margin:0; font-size:17px; font-weight:600;">Vidéo dans ma bibliothèque</h2>
        <span id="vcount" class="pill">0 vidéo</span>
      </div>
      <div id="videos" style="display:flex; flex-direction:column; gap:10px; margin-bottom:28px;"></div>
    </div>

    <div id="openInRow" class="panel openIn hidden">
      <span>Ouvrir les vidéos dans</span>
      <div class="seg">
        <button type="button" data-open="web">Navigateur</button>
        <button type="button" data-open="swiftfin">App Swiftfin</button>
      </div>
    </div>

    <div class="syncRow" style="display:flex; align-items:center; gap:16px;">
      <button id="syncBtn" class="btn-accent" style="display:flex; align-items:center; gap:10px; padding:14px 24px; font-size:15px; border-radius:13px;">
        <svg id="syncIcon" width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><polyline points="21 3 21 9 15 9"/></svg>
        Synchroniser maintenant
      </button>
      <span id="syncStatus" style="font-size:13px; color:var(--text-dim);">Jamais synchronisé</span>
    </div>
    </div><!-- /mainView -->

    <!-- CHANNEL PAGE -->
    <div id="channelView" class="hidden">
      <button id="backBtn" class="btn-ghost backBtn" type="button">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><polyline points="15 18 9 12 15 6"/></svg>
        Mes chaînes
      </button>
      <div class="chead">
        <div id="cAvatar"></div>
        <div style="flex:1; min-width:0;">
          <h1 id="cName"></h1>
          <p id="cMeta"></p>
        </div>
        <a id="cYt" class="btn-ghost ytLink" target="_blank" rel="noopener" aria-label="Voir sur YouTube">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/><polyline points="15 3 21 3 21 9"/><line x1="10" y1="14" x2="21" y2="3"/></svg>
          <span>YouTube</span>
        </a>
      </div>
      <div id="cVideos" class="vgrid"></div>
    </div>
  </div>
</div>

<div id="confirmModal" class="overlay hidden">
  <div class="modal">
    <p style="margin:0 0 4px; font-size:16px; font-weight:600;">Avez-vous ajouté tous les youtubeurs de votre choix ?</p>
    <div style="display:flex; gap:12px; justify-content:center; margin-top:18px;">
      <button id="confirmYes" class="btn-accent" style="padding:11px 26px;">Oui</button>
      <button id="confirmNo" class="btn-ghost" style="padding:11px 26px;">Non</button>
    </div>
  </div>
</div>

<script>
// iOS Safari still pinch-zooms despite the viewport meta; its proprietary gesture events can be cancelled.
["gesturestart", "gesturechange", "gestureend"].forEach(function (t) {
  document.addEventListener(t, function (e) { e.preventDefault(); }, { passive: false });
});
(function () {
  var token = localStorage.getItem("ytf_token");
  var userId = localStorage.getItem("ytf_uid");
  var userName = localStorage.getItem("ytf_uname");
  // Link shared from another app (Android share sheet via the manifest's
  // share_target, or an iOS Shortcut opening /JellyTuber?url=...). Stashed in
  // sessionStorage so it survives the login step, and stripped from the
  // address bar so a reload doesn't add it again.
  (function () {
    var qs = new URLSearchParams(location.search);
    var shared = ["url", "text", "title"].map(function (k) { return qs.get(k) || ""; }).join(" ").trim();
    if (shared) {
      sessionStorage.setItem("jt_share", shared);
      history.replaceState(null, "", location.pathname);
    }
  })();
  var PALETTE = ['#22c3b6','#2a9fd6','#7c6cf0','#e0738a','#e0a13b','#5bbf6a','#d76b4b','#4aa3a0','#b06fd6'];

  var SUN = '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4"/>';
  var MOON = '<path d="M21 12.8A9 9 0 1 1 11.2 3 7 7 0 0 0 21 12.8z"/>';

  function show(id, on) { document.getElementById(id).classList.toggle("hidden", !on); }
  // Re-triggerable one-shot animation (restart even if already applied).
  // The class comes off once it has played: a finished "both"-filled
  // animation would otherwise pin transform and block the press effect.
  function animate(el, cls) {
    el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls);
    el.addEventListener("animationend", function done(e) {
      if (e.target !== el && cls !== "flip") return;
      el.classList.remove(cls); el.removeEventListener("animationend", done);
    });
  }
  function enter(el, i) { el.style.setProperty("--i", Math.min(i || 0, 12)); animate(el, "enter"); }
  // Fold an item away, then run next (e.g. reload the list).
  function leave(el, next) {
    el.style.maxHeight = el.offsetHeight + "px"; void el.offsetWidth;
    el.classList.remove("enter"); el.classList.add("leaving");
    setTimeout(function () { if (next) next(); else el.remove(); }, 380);
  }
  var seenChannels = {}, seenVideos = {};
  function esc(s){ return (s||"").replace(/[&<>"]/g,function(m){return ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"})[m];}); }

  function applyTheme(t) {
    document.documentElement.classList.toggle("light", t === "light");
    document.getElementById("themeIcon").innerHTML = t === "light" ? MOON : SUN;
    document.getElementById("themeLabel").textContent = t === "light" ? "Mode sombre" : "Mode clair";
    localStorage.setItem("ytf_theme", t);
  }

  function avatar(name, i, img) {
    var c = PALETTE[i % PALETTE.length];
    var d = document.createElement("div");
    d.className = "avatar";
    function fallback() {
      d.textContent = "";
      d.style.overflow = "visible";
      d.style.background = c + "26"; d.style.color = c; d.style.border = "1px solid " + c + "40";
      d.textContent = (name || "?").trim().charAt(0).toUpperCase();
    }
    if (img) {
      d.style.overflow = "hidden";
      d.style.background = "var(--bg-elev2)";
      d.style.border = "1px solid var(--border)";
      var im = document.createElement("img");
      im.src = img; im.alt = name || ""; im.loading = "lazy"; im.referrerPolicy = "no-referrer";
      im.style.cssText = "width:100%;height:100%;object-fit:cover;display:block;";
      im.addEventListener("error", function () { if (im.parentNode === d) d.removeChild(im); fallback(); });
      d.appendChild(im);
    } else {
      fallback();
    }
    return d;
  }

  function api(path, opts) {
    opts = opts || {};
    opts.headers = Object.assign({ "Authorization": 'MediaBrowser Token="' + token + '"', "Content-Type": "application/json" }, opts.headers || {});
    return fetch(path, opts).then(function (r) {
      if (r.status === 401) {
        logout();
        document.getElementById("loginErr").textContent = "Session expirée, veuillez vous reconnecter.";
        throw new Error(r.status);
      }
      if (!r.ok) { throw new Error(r.status); }
      return r.status === 204 ? null : r.json();
    });
  }

  function login() {
    var u = document.getElementById("u").value, p = document.getElementById("p").value;
    fetch("/Users/AuthenticateByName", {
      method: "POST",
      headers: { "Content-Type": "application/json",
        "Authorization": 'MediaBrowser Client="JellyTuber", Device="Web", DeviceId="jellytuber-manager", Version="1.0.0"' },
      body: JSON.stringify({ Username: u, Pw: p })
    }).then(function (r){ if(!r.ok) throw new Error("auth"); return r.json(); })
      .then(function (d) {
        token = d.AccessToken; userId = d.User.Id; userName = d.User.Name;
        localStorage.setItem("ytf_token", token);
        localStorage.setItem("ytf_uid", userId);
        localStorage.setItem("ytf_uname", userName);
        enterApp();
      }).catch(function () {
        document.getElementById("loginErr").textContent = "Connexion échouée. Vérifiez vos identifiants.";
      });
  }

  function logout() {
    localStorage.removeItem("ytf_token"); localStorage.removeItem("ytf_uid"); localStorage.removeItem("ytf_uname");
    token = userId = userName = null;
    show("app", false); show("themeBtn", false); show("login", true);
    if (location.hash) history.replaceState(null, "", location.pathname + location.search);
  }

  function enterApp() {
    show("login", false); show("app", true); show("themeBtn", true); loadMine(); loadVideos(); loadShareCode();
    renderOpenIn();
    if (!serverId) {
      fetch("/System/Info/Public").then(function (r) { return r.json(); })
        .then(function (i) { serverId = field(i, "Id"); }).catch(function () {});
    }
    var shared = sessionStorage.getItem("jt_share");
    if (shared) {
      sessionStorage.removeItem("jt_share");
      // Keep only the YouTube link: apps often share "Title https://youtu.be/..".
      var m = shared.match(/https?:\/\/\S*(youtube\.com|youtu\.be)\S*/i);
      switchTab("video");
      document.getElementById("vq").value = m ? m[0] : shared;
      addVideo(function () {
        api("/JellyTuber/User/Sync", { method: "POST" }).catch(function () {});
        toast("Vidéo ajoutée — synchronisation lancée.");
      });
    }
    route();
  }

  function field(o, k) { return o[k] != null ? o[k] : o[k.charAt(0).toLowerCase() + k.slice(1)]; }

  function loadMine() {
    api("/JellyTuber/User/Channels").then(function (list) {
      var box = document.getElementById("mine"); box.innerHTML = "";
      var n = list.length;
      document.getElementById("count").textContent = n + (n > 1 ? " chaînes" : " chaîne");
      if (!n) { box.innerHTML = '<p style="color:var(--text-dim); font-size:14px;">Aucune chaîne. Recherchez ci-dessus pour en ajouter.</p>'; return; }
      list.forEach(function (c, idx) {
        var name = field(c, "Name"), cid = field(c, "ChannelId"), ex = field(c, "ExcludeShorts");
        var thumb = field(c, "Thumbnail");
        var maxVideos = field(c, "MaxVideos") || 25;
        var card = document.createElement("div");
        card.className = "panel rowline chancard"; card.style.padding = "14px 16px";
        // Only new cards rise in: a reload after a change leaves the rest still.
        if (!seenChannels[cid]) { seenChannels[cid] = 1; enter(card, idx); }
        card.appendChild(avatar(name, idx + 2, thumb));
        var nm = document.createElement("span"); nm.className = "name"; nm.style.fontSize = "15px"; nm.textContent = name;
        card.appendChild(nm);
        card.appendChild(chevron());
        channels[cid] = { name: name, thumb: thumb, idx: idx + 2, max: function () { return maxVideos; }, ex: function () { return ex; } };
        // The whole card opens the channel page, except its own controls.
        card.addEventListener("click", function (e) { if (!e.target.closest("button")) openChannel(cid); });

        var mv = document.createElement("div"); mv.className = "maxvid";
        mv.innerHTML = '<span>Vidéos</span><button type="button" aria-label="Moins de vidéos">−</button><b></b><button type="button" aria-label="Plus de vidéos">+</button>';
        (function (minus, val, plus) {
          var MIN = 5, MAX = 50, STEP = 5, saveTimer = null;
          function render() {
            if (val.textContent && val.textContent != maxVideos) animate(val, "bump");
            val.textContent = maxVideos;
            minus.disabled = maxVideos <= MIN; plus.disabled = maxVideos >= MAX;
          }
          // Snap to the step grid (a value saved as e.g. 23 goes to 20 / 25),
          // and only save once the clicks stop: every save re-queues a sync.
          function change(dir) {
            maxVideos = dir > 0
              ? Math.min(MAX, (Math.floor(maxVideos / STEP) + 1) * STEP)
              : Math.max(MIN, (Math.ceil(maxVideos / STEP) - 1) * STEP);
            render();
            clearTimeout(saveTimer);
            saveTimer = setTimeout(function () {
              var n = maxVideos;
              api("/JellyTuber/User/SetMaxVideos", { method: "POST", body: JSON.stringify({ channelId: cid, maxVideos: n }) })
                .then(function () { toast("Limite mise à jour : " + n + " vidéos."); });
            }, 800);
          }
          minus.addEventListener("click", function () { change(-1); });
          plus.addEventListener("click", function () { change(1); });
          render();
        })(mv.querySelectorAll("button")[0], mv.querySelector("b"), mv.querySelectorAll("button")[1]);
        card.appendChild(mv);

        var sh = document.createElement("button"); sh.className = "shorts" + (ex ? " on" : "");
        sh.innerHTML = '<span class="dot"></span>Exclure les Shorts';
        sh.addEventListener("click", function () {
          ex = !ex; sh.className = "shorts" + (ex ? " on" : ""); animate(sh, "flip");
          api("/JellyTuber/User/ToggleShorts", { method: "POST", body: JSON.stringify({ channelId: cid, excludeShorts: ex }) });
        });
        card.appendChild(sh);

        var rm = document.createElement("button"); rm.className = "remove"; rm.textContent = "Retirer";
        rm.addEventListener("click", function () {
          rm.disabled = true;
          api("/JellyTuber/User/Remove", { method: "POST", body: JSON.stringify({ channelId: cid }) })
            .then(function () { delete seenChannels[cid]; leave(card, loadMine); })
            .catch(function () { rm.disabled = false; });
        });
        card.appendChild(rm);
        box.appendChild(card);
      });
    });
  }

  // ---- Opening a synced video in Jellyfin ----
  // Web client by default. On iPhone/iPad, Swiftfin (the native Jellyfin app)
  // opens swiftfin://{serverId}/{userId}/item/{itemId}; the official Android
  // app has no deep links, so the web client is the only option there.
  var isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  var serverId = null;
  function openInPref() { try { return localStorage.getItem("jt_open") || "web"; } catch (e) { return "web"; } }
  function renderOpenIn() {
    show("openInRow", isIOS);
    var pref = openInPref();
    document.querySelectorAll("#openInRow .seg button").forEach(function (b) { b.classList.toggle("on", b.getAttribute("data-open") === pref); });
  }
  function jellyfinLink(jid) {
    if (isIOS && serverId && userId && openInPref() === "swiftfin") {
      return "swiftfin://" + serverId + "/" + userId + "/item/" + jid;
    }
    return "/web/#/details?id=" + encodeURIComponent(jid);
  }
  function chevron() {
    var c = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    c.setAttribute("class", "chev"); c.setAttribute("width", "16"); c.setAttribute("height", "16");
    c.setAttribute("viewBox", "0 0 24 24"); c.setAttribute("fill", "none"); c.setAttribute("stroke", "currentColor");
    c.setAttribute("stroke-width", "2.4"); c.setAttribute("stroke-linecap", "round"); c.setAttribute("stroke-linejoin", "round");
    c.innerHTML = '<polyline points="9 18 15 12 9 6"/>';
    return c;
  }

  // ---- Channel page (#chaine=<id>): preview of the channel's latest videos ----
  var channels = {}, cameFromList = false, channelReq = 0;
  function openChannel(cid) { cameFromList = true; location.hash = "chaine=" + encodeURIComponent(cid); }
  function closeChannel() {
    if (cameFromList) { cameFromList = false; history.back(); }
    else { history.replaceState(null, "", location.pathname + location.search); route(); }
  }
  function route() {
    var m = location.hash.match(/^#chaine=(.+)$/);
    var cid = m ? decodeURIComponent(m[1]) : null;
    show("mainView", !cid); show("channelView", !!cid);
    if (!cid) { cameFromList = false; return; }
    window.scrollTo(0, 0);
    animate(document.querySelector(".chead"), "enter");
    renderChannel(cid);
  }
  function fmtAgo(iso) {
    var d = Math.floor((Date.now() - new Date(iso).getTime()) / 86400000);
    if (isNaN(d)) return "";
    if (d <= 0) return "Aujourd'hui";
    if (d === 1) return "Hier";
    if (d < 7) return "Il y a " + d + " jours";
    if (d < 30) { var w = Math.floor(d / 7); return "Il y a " + w + (w > 1 ? " semaines" : " semaine"); }
    if (d < 365) return "Il y a " + Math.floor(d / 30) + " mois";
    var y = Math.floor(d / 365); return "Il y a " + y + (y > 1 ? " ans" : " an");
  }
  function setChannelHeader(name, thumb, idx, meta) {
    var a = document.getElementById("cAvatar"); a.innerHTML = ""; a.appendChild(avatar(name, idx, thumb));
    document.getElementById("cName").textContent = name || "";
    document.getElementById("cMeta").textContent = meta;
  }
  function metaText(n, ex) { return n + " dernières vidéos" + (ex ? " · sans les Shorts" : ""); }
  function renderChannel(cid) {
    var req = ++channelReq, c = channels[cid];
    var box = document.getElementById("cVideos");
    document.getElementById("cYt").href = "https://www.youtube.com/channel/" + encodeURIComponent(cid);
    setChannelHeader(c ? c.name : "", c ? c.thumb : "", c ? c.idx : 0, c ? metaText(c.max(), c.ex()) : "");
    var sk = "";
    for (var i = 0; i < Math.min(c ? c.max() : 8, 12); i++) {
      sk += '<div class="vg"><div class="vg-thumb sk" style="border-radius:0"></div><div class="vg-body">'
        + '<div class="sk" style="height:12px; margin-bottom:6px"></div><div class="sk" style="height:12px; width:60%"></div></div></div>';
    }
    box.innerHTML = sk;
    api("/JellyTuber/User/ChannelVideos?channelId=" + encodeURIComponent(cid)).then(function (r) {
      if (req !== channelReq) return;
      var list = field(r, "Videos") || [];
      setChannelHeader(field(r, "Name"), field(r, "Thumbnail"), c ? c.idx : 0, metaText(field(r, "MaxVideos"), field(r, "ExcludeShorts")));
      box.innerHTML = "";
      if (!list.length) { box.innerHTML = '<p style="color:var(--text-dim); font-size:14px; margin:0;">Aucune vidéo trouvée pour cette chaîne.</p>'; return; }
      list.forEach(function (v) {
        var vid = field(v, "VideoId"), dur = fmtDuration(field(v, "DurationSeconds"));
        var jid = field(v, "JellyfinId");
        var a = document.createElement("a"); a.className = "vg";
        // Synced: land on the video in Jellyfin. Not synced yet: preview it on YouTube.
        if (jid) { a.href = jellyfinLink(jid); }
        else { a.href = "https://www.youtube.com/watch?v=" + encodeURIComponent(vid); a.target = "_blank"; a.rel = "noopener"; }
        a.innerHTML = '<div class="vg-thumb"><img loading="lazy" referrerpolicy="no-referrer" alt="" src="https://i.ytimg.com/vi/' + esc(vid) + '/mqdefault.jpg">'
          + (field(v, "InLibrary") ? '<span class="vg-lib">Dans Jellyfin</span>' : '')
          + (dur ? '<span class="vg-dur">' + esc(dur) + '</span>' : '') + '</div>'
          + '<div class="vg-body"><div class="vg-title">' + esc(field(v, "Title")) + '</div>'
          + '<div class="vg-meta">' + esc(fmtAgo(field(v, "PublishedAt"))) + (jid ? '' : ' · YouTube') + '</div></div>';
        enter(a, box.children.length);
        box.appendChild(a);
      });
    }).catch(function (e) {
      if (req !== channelReq) return;
      box.innerHTML = '<p style="color:var(--text-dim); font-size:14px; margin:0;">Impossible de charger les vidéos (' + esc(e && e.message) + ').</p>';
    });
  }

  // Search as you type: fires once typing pauses, and only the latest
  // query's answer is shown (an older, slower one is dropped).
  var searchSeq = 0, searchTimer = null, lastSearch = "";
  function searchSoon() {
    clearTimeout(searchTimer);
    var q = document.getElementById("q").value.trim();
    if (q.length < 2) { searchSeq++; lastSearch = ""; document.getElementById("results").innerHTML = ""; return; }
    searchTimer = setTimeout(search, 600);
  }
  function search() {
    clearTimeout(searchTimer);
    var q = document.getElementById("q").value.trim(); if (q.length < 2 || q === lastSearch) return;
    lastSearch = q;
    var seq = ++searchSeq;
    var box = document.getElementById("results");
    box.innerHTML = '<p style="color:var(--text-dim); font-size:14px;">Recherche…</p>';
    api("/JellyTuber/User/Search", { method: "POST", body: JSON.stringify({ query: q }) }).then(function (list) {
      if (seq !== searchSeq) return;
      box.innerHTML = "";
      if (!list.length) { box.innerHTML = '<p style="color:var(--text-dim); font-size:14px;">Aucune chaîne trouvée.</p>'; return; }
      list.forEach(function (c, i) {
        var name = field(c, "Name"), cid = field(c, "ChannelId");
        var thumb = field(c, "Thumbnail");
        var row = document.createElement("div");
        row.className = "rowline"; row.style.cssText = "padding:10px 12px; border-radius:13px; background:var(--bg-elev2); border:1px solid transparent;";
        enter(row, i);
        row.appendChild(avatar(name, i, thumb));
        var nm = document.createElement("span"); nm.className = "name"; nm.style.fontSize = "14.5px"; nm.textContent = name;
        row.appendChild(nm);
        var add = document.createElement("button"); add.className = "btn-accent"; add.style.cssText = "padding:8px 16px; font-size:13px; flex:none;"; add.textContent = "Ajouter";
        add.addEventListener("click", function () {
          api("/JellyTuber/User/Add", { method: "POST", body: JSON.stringify({ channelId: cid, name: name, thumbnail: thumb }) })
            .then(function () {
              add.textContent = "Ajoutée ✓"; add.disabled = true; animate(add, "pop");
              loadMine(); setTimeout(function () { leave(row); }, 500);
            });
        });
        row.appendChild(add);
        box.appendChild(row);
      });
    }).catch(function (e) {
      if (seq !== searchSeq) return;
      lastSearch = "";
      box.innerHTML = '<p style="color:var(--text-dim); font-size:14px;">Recherche échouée (' + (e && e.message) + ').</p>';
    });
  }

  // Slide the overlay placeholder only when it overflows, by exactly the
  // hidden part; re-measured when it becomes visible or the width changes.
  function fitPlaceholder() {
    var box = document.getElementById("vqPh"), inner = box.firstElementChild;
    var d = inner.scrollWidth - box.clientWidth;
    box.classList.toggle("run", box.clientWidth > 0 && d > 2);
    box.style.setProperty("--d", -(d + 4) + "px");
    box.style.setProperty("--dur", (4 + d / 25).toFixed(1) + "s");
  }
  window.addEventListener("resize", fitPlaceholder);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(fitPlaceholder);

  function switchTab(which) {
    var isVideo = which === "video";
    show("tabVideo", isVideo);
    if (isVideo) fitPlaceholder();
    show("tabChaine", !isVideo);
    animate(document.getElementById(isVideo ? "tabVideo" : "tabChaine"), "enter");
    document.getElementById("tabBtnVideo").classList.toggle("active", isVideo);
    document.getElementById("tabBtnChaine").classList.toggle("active", !isVideo);
  }

  function loadVideos() {
    api("/JellyTuber/User/Videos").then(function (list) {
      var box = document.getElementById("videos"); box.innerHTML = "";
      var n = list.length;
      document.getElementById("vcount").textContent = n + (n > 1 ? " vidéos" : " vidéo");
      if (!n) { box.innerHTML = '<p style="color:var(--text-dim); font-size:14px;">Aucune vidéo. Collez un lien ci-dessus pour en ajouter.</p>'; return; }
      list.forEach(function (v, idx) {
        var name = field(v, "Title"), vid = field(v, "VideoId"), thumb = field(v, "Thumbnail"), jid = field(v, "JellyfinId");
        var card = document.createElement("div");
        card.className = "panel rowline vcard linkcard"; card.style.padding = "14px 16px";
        // Synced: open it in Jellyfin. Not synced yet: preview it on YouTube.
        card.title = jid ? "Ouvrir dans Jellyfin" : "Pas encore synchronisée — ouvrir sur YouTube";
        card.addEventListener("click", function (e) {
          if (e.target.closest("button")) return;
          if (jid) location.href = jellyfinLink(jid);
          else window.open("https://www.youtube.com/watch?v=" + encodeURIComponent(vid), "_blank", "noopener");
        });
        if (!seenVideos[vid]) { seenVideos[vid] = 1; enter(card, idx); }
        card.appendChild(avatar(name, idx + 1, thumb));
        var nm = document.createElement("span"); nm.className = "name"; nm.style.fontSize = "15px"; nm.textContent = name;
        card.appendChild(nm);
        card.appendChild(chevron());

        var rm = document.createElement("button"); rm.className = "remove"; rm.textContent = "Retirer";
        rm.addEventListener("click", function () {
          rm.disabled = true;
          api("/JellyTuber/User/RemoveVideo", { method: "POST", body: JSON.stringify({ videoId: vid }) })
            .then(function () { delete seenVideos[vid]; leave(card, loadVideos); })
            .catch(function () { rm.disabled = false; });
        });
        card.appendChild(rm);
        box.appendChild(card);
      });
    });
  }

  var shareLink = "";
  function setShareCode(r) {
    shareLink = location.origin + "/JellyTuber/Share?code=" + encodeURIComponent(field(r, "Code")) + "&url=";
    document.getElementById("shareLinkText").textContent = shareLink;
    document.getElementById("copyShareBtn").disabled = false;
  }
  function loadShareCode() { api("/JellyTuber/User/ShareCode").then(setShareCode); }
  function regenShareCode() {
    if (!confirm("Générer un nouveau code ? L'ancien ne fonctionnera plus : il faudra mettre à jour le raccourci.")) return;
    api("/JellyTuber/User/ShareCode/Regenerate", { method: "POST" }).then(function (r) {
      setShareCode(r); toast("Nouveau code généré.");
    });
  }
  function copyShareLink() {
    if (!shareLink) return;
    (navigator.clipboard ? navigator.clipboard.writeText(shareLink) : Promise.reject())
      .catch(function () {
        // No async clipboard (plain http, old iOS): copy from a throwaway field.
        var ta = document.createElement("textarea");
        ta.value = shareLink; ta.setAttribute("readonly", ""); ta.style.cssText = "position:fixed; opacity:0;";
        document.body.appendChild(ta); ta.select(); document.execCommand("copy"); ta.remove();
      })
      .then(function () { animate(document.getElementById("copyShareBtn"), "pop"); toast("Lien copié."); });
  }

  function addVideo(onAdded) {
    var url = document.getElementById("vq").value.trim();
    var msg = document.getElementById("vmsg");
    if (!url) return;
    msg.style.color = "var(--text-dim)"; msg.textContent = "Ajout…";
    api("/JellyTuber/User/AddVideo", { method: "POST", body: JSON.stringify({ url: url }) })
      .then(function (r) {
        var status = r && field(r, "Status");
        if (status === "exists") {
          msg.style.color = "var(--text-dim)";
          msg.textContent = "Cette vidéo est déjà dans votre bibliothèque.";
        } else if (status === "short") {
          msg.style.color = "#e0738a";
          msg.textContent = "C'est un Short — non ajouté (l'application exclut les Shorts).";
        } else {
          msg.textContent = ""; document.getElementById("vq").value = ""; lastVideoInput = "";
          if (typeof onAdded === "function") { onAdded(); } else { toast("Vidéo ajoutée."); }
        }
        loadVideos();
      })
      .catch(function () {
        lastVideoInput = "";
        msg.style.color = "#e0738a";
        msg.textContent = "Échec de l'ajout. Vérifiez que le lien est une vidéo YouTube valide.";
      });
  }

  // One box for both: a YouTube link is added straight away, anything else
  // is searched by name.
  function isVideoLink(t) { return /^https?:\/\//i.test(t) || /(youtube\.com|youtu\.be)\//i.test(t); }
  var videoSeq = 0, videoTimer = null, lastVideoInput = "";
  function submitVideoSoon() {
    clearTimeout(videoTimer);
    var t = document.getElementById("vq").value.trim();
    if (t.length < 2) {
      videoSeq++; lastVideoInput = "";
      document.getElementById("vresults").innerHTML = ""; document.getElementById("vmsg").textContent = "";
      return;
    }
    // A pasted link lands in one go: add it almost at once; a typed name waits for a pause.
    videoTimer = setTimeout(submitVideo, isVideoLink(t) ? 250 : 600);
  }
  function submitVideo() {
    clearTimeout(videoTimer);
    var t = document.getElementById("vq").value.trim();
    if (t.length < 2 || t === lastVideoInput) return;
    lastVideoInput = t;
    if (isVideoLink(t)) { videoSeq++; document.getElementById("vresults").innerHTML = ""; addVideo(); } else { searchVideos(t); }
  }
  function fmtDuration(sec) {
    if (sec == null) return "";
    var h = Math.floor(sec / 3600), m = Math.floor(sec % 3600 / 60), s = sec % 60;
    return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(s).padStart(2, "0");
  }
  function searchVideos(q) {
    var box = document.getElementById("vresults"), msg = document.getElementById("vmsg");
    var seq = ++videoSeq;
    msg.textContent = ""; box.innerHTML = '<p style="color:var(--text-dim); font-size:14px; margin:4px 0 0;">Recherche…</p>';
    api("/JellyTuber/User/SearchVideos", { method: "POST", body: JSON.stringify({ query: q }) }).then(function (list) {
      if (seq !== videoSeq) return;
      box.innerHTML = "";
      if (!list.length) { box.innerHTML = '<p style="color:var(--text-dim); font-size:14px; margin:4px 0 0;">Aucune vidéo trouvée.</p>'; return; }
      list.forEach(function (v) {
        var vid = field(v, "VideoId"), title = field(v, "Title");
        var row = document.createElement("div");
        row.className = "rowline"; row.style.cssText = "padding:10px 12px; border-radius:13px; background:var(--bg-elev2); flex-wrap:nowrap;";
        enter(row, box.children.length);
        var im = document.createElement("img");
        im.className = "vthumb"; im.src = field(v, "Thumbnail") || ""; im.alt = ""; im.loading = "lazy"; im.referrerPolicy = "no-referrer";
        row.appendChild(im);
        var txt = document.createElement("div"); txt.style.cssText = "flex:1; min-width:0;";
        var t = document.createElement("div"); t.className = "vtitle"; t.textContent = title;
        var meta = document.createElement("div"); meta.className = "vmeta";
        meta.textContent = [field(v, "ChannelTitle"), fmtDuration(field(v, "DurationSeconds"))].filter(Boolean).join(" · ");
        txt.appendChild(t); txt.appendChild(meta); row.appendChild(txt);
        var add = document.createElement("button"); add.className = "btn-accent"; add.style.cssText = "padding:8px 14px; font-size:13px; flex:none;";
        function done(label) { add.textContent = label; add.disabled = true; add.style.opacity = ".55"; add.style.cursor = "default"; }
        if (field(v, "AlreadyAdded")) { done("Déjà ajoutée"); } else { add.textContent = "Ajouter"; }
        add.addEventListener("click", function () {
          if (add.disabled) return;
          add.disabled = true; add.textContent = "Ajout…";
          api("/JellyTuber/User/AddVideo", { method: "POST", body: JSON.stringify({ url: "https://www.youtube.com/watch?v=" + vid }) })
            .then(function (r) {
              var status = r && field(r, "Status");
              if (status === "short") { done("Short"); toast("C'est un Short — non ajouté."); }
              else if (status === "exists") { done("Déjà ajoutée"); }
              else { done("Ajoutée ✓"); animate(add, "pop"); toast("Vidéo ajoutée."); }
              loadVideos();
            })
            .catch(function () { add.disabled = false; add.textContent = "Ajouter"; toast("Échec de l'ajout."); });
        });
        row.appendChild(add);
        box.appendChild(row);
      });
    }).catch(function (e) {
      if (seq !== videoSeq) return;
      lastVideoInput = "";
      box.innerHTML = '<p style="color:var(--text-dim); font-size:14px; margin:4px 0 0;">Recherche échouée (' + (e && e.message) + ').</p>';
    });
  }

  function toast(msg) {
    var t = document.createElement("div"); t.className = "toast"; t.textContent = msg;
    document.body.appendChild(t);
    setTimeout(function () { t.classList.add("out"); setTimeout(function () { t.remove(); }, 260); }, 3300);
  }
  function showModal(on) { show("confirmModal", on); }

  function doSync() {
    var icon = document.getElementById("syncIcon"); icon.classList.add("spin");
    api("/JellyTuber/User/Sync", { method: "POST" }).then(function () {
      var d = new Date();
      var t = String(d.getHours()).padStart(2,"0") + ":" + String(d.getMinutes()).padStart(2,"0");
      document.getElementById("syncStatus").textContent = "Dernière synchro à " + t;
      toast("Synchronisation lancée.");
    }).catch(function (e) { toast("Échec (" + (e && e.message) + ")."); })
      .then(function () { setTimeout(function(){ icon.classList.remove("spin"); }, 1200); });
  }

  document.getElementById("loginBtn").addEventListener("click", login);
  document.getElementById("logoutBtn").addEventListener("click", logout);
  document.getElementById("q").addEventListener("input", searchSoon);
  document.getElementById("backBtn").addEventListener("click", closeChannel);
  document.querySelectorAll("#openInRow .seg button").forEach(function (b) {
    b.addEventListener("click", function () {
      try { localStorage.setItem("jt_open", b.getAttribute("data-open")); } catch (e) {}
      renderOpenIn(); animate(b, "pop");
    });
  });
  window.addEventListener("hashchange", function () { if (token) route(); });
  document.getElementById("tabBtnChaine").addEventListener("click", function () { switchTab("chaine"); });
  document.getElementById("tabBtnVideo").addEventListener("click", function () { switchTab("video"); });
  document.getElementById("vq").addEventListener("input", submitVideoSoon);
  document.getElementById("copyShareBtn").addEventListener("click", copyShareLink);
  document.getElementById("regenShareBtn").addEventListener("click", regenShareCode);
  document.getElementById("vq").addEventListener("keydown", function (e) { if (e.key === "Enter") { submitVideo(); e.target.blur(); } });
  document.getElementById("themeBtn").addEventListener("click", function () {
    applyTheme(document.documentElement.classList.contains("light") ? "dark" : "light");
  });
  document.getElementById("syncBtn").addEventListener("click", function () { showModal(true); });
  document.getElementById("confirmNo").addEventListener("click", function () { showModal(false); });
  document.getElementById("confirmYes").addEventListener("click", function () { showModal(false); doSync(); });
  document.getElementById("q").addEventListener("keydown", function (e) { if (e.key === "Enter") { search(); e.target.blur(); } });
  document.getElementById("p").addEventListener("keydown", function (e) { if (e.key === "Enter") login(); });

  applyTheme(localStorage.getItem("ytf_theme") || "dark");
  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/JellyTuber/sw.js", { scope: "/JellyTuber" }).catch(function () {});
  }
  if (token && userId) { enterApp(); }
})();
</script>
</body>
</html>
""";
}
