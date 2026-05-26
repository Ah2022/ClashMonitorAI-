# ClashResolveAI 🚀

**Next-Gen Real-Time Clash Detection & Resolution for Revit 2024**

ClashResolveAI is a professional-grade BIM coordination engine that brings real-time clash monitoring, spatial intelligence, and automated resolution to Autodesk Revit. Designed for MEP, Structural, and Architectural coordination, it leverages advanced geometry algorithms and AI to ensure your models are clash-free as you design.

---

## ✨ Key Features

### 📡 Clash Radar (Live Monitor)
*   **Real-Time Monitoring:** Scans your active elements and newly drawn components instantly.
*   **Lightweight Geometry Engine:** Uses AABB (Axis-Aligned Bounding Box) checks for maximum performance without lagging Revit.
*   **Instant Feedback:** Visual toast notifications inform you immediately if a clash is detected ("⚠️ 3 Clashes Detected") or if your work is clear ("✅ No Clash").
*   **Linked Model Support:** Automatically monitors host elements against linked structural models.

### 🧠 Intelligent Resolution
*   **Spatial Hash Grid:** Ultra-fast indexing for rapid neighbor lookups in large projects.
*   **Rules Engine:** Customisable clash rules (e.g., Hospital, Data Center) with specific clearance requirements.
*   **Auto-Resolve:** Leverages geometric intelligence to propose or apply fixes for common clearance issues.

### 📊 Coordination Dashboard
*   **Visual List:** All detected clashes are listed in an interactive UI panel.
*   **One-Click Navigation:** Select a clash to automatically zoom and highlight the conflicting elements.
*   **BCF 2.1 Export:** Export clashes to industry-standard BCF format for collaboration.
*   **Reporting:** Generate professional Excel and Word reports with clash snapshots.

---

## 🚀 Getting Started

### Installation
1.  Ensure you have **Autodesk Revit 2024** installed.
2.  Download the latest release and copy the contents to:
    `%APPDATA%\Autodesk\Revit\Addins\2024\`
3.  Restart Revit. You will see a new **ClashResolveAI** tab in the ribbon.

### How to Use
1.  **Activate Clash Radar:** Click the **Live Monitor** button in the ribbon. The Radar Panel will appear on the right.
2.  **Design & Coordinate:** As you draw or modify MEP/Structural elements, the engine scans the immediate vicinity.
3.  **Check Notifications:** 
    *   If no clashes are found, a green **"No Clash"** toast appears.
    *   If a clash occurs, an orange alert shows the number of clashes.
4.  **Review Clashes:** Open the **Dashboard** to see detailed information about every clash, including gap measurements and severity levels.

---

## 🛠 Tech Stack
*   **C# / .NET 4.8** (Revit 2024 API)
*   **Spatial Hash Grid** for $O(1)$ spatial queries.
*   **SQLite** for persistent clash tracking.
*   **OpenAI SDK** for intelligent resolution suggestions.
*   **ClosedXML & EPPlus** for professional report generation.

---

## 📄 License
© 2026 BIM Coordination Engineering. All rights reserved.

---

*“Empowering BIM Coordinators with Real-Time Intelligence.”*
