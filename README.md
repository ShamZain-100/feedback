# Shelfnote Feedback

Customer feedback kiosk and manager dashboard for retail stores, packaged as a single Windows executable.

- **Kiosk**: customers rate their visit (1–5), tap what stood out, answer a 0–10 recommend question, and can leave a comment, receipt number and email.
- **Dashboard**: response count, average rating, CSAT and NPS against the previous period, a daily trend, the topics driving the score, and a comment inbox where low ratings arrive as *New* for follow-up. Exports to CSV.
- **Setup**: business name, store list, the store each device belongs to, and the address tablets use to connect.

## Run it

Double-click `ShelfnoteFeedback.exe`. It starts a small local server, opens the dashboard, and shows the address store tablets on the same Wi-Fi can open (for example `http://192.168.1.20:8787/#kiosk`). Keep its window open while collecting feedback.

Responses are saved in `Documents\Shelfnote Feedback\` on that PC.

On first run Windows may show a SmartScreen warning (the exe is unsigned: choose *More info → Run anyway*) and a firewall prompt (allow *Private networks* so tablets can connect).

Requires Windows 10 or 11 (uses the built-in .NET Framework 4).

## Build

No SDK needed; the C# compiler ships with Windows:

```powershell
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /optimize+ /out:ShelfnoteFeedback.exe /win32icon:src\app.ico `
  /resource:src\index.html,index.html /r:System.Web.Extensions.dll src\Shelfnote.cs
```

## Files

- `src/Shelfnote.cs` — HTTP server and JSON storage
- `src/index.html` — the app (kiosk, dashboard, setup), embedded into the exe at build time
- `src/app.ico` — program icon
