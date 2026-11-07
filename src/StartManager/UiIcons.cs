using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace StartManager;

internal static class UiIcons
{
    // Icons are drawn on a 16×16 grid and scaled with the interface font.
    public static Control Create(string name, double size)
    {
        var icon = Create(name);
        return size <= 16 ? icon : new Viewbox { Width = size, Height = size, Child = icon, VerticalAlignment = VerticalAlignment.Center };
    }

    public static Control Create(string name)
    {
        var canvas = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        void Draw(string data, string color, bool fill = false, double width = 1.4) => canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            // The non-zero rule: shapes made of overlapping rectangles keep the overlaps filled (even-odd punched holes there).
            Data = Geometry.Parse(data.StartsWith('F') ? data : "F1 " + data), Stroke = fill ? null : ColorBrush(color),
            Fill = fill ? ColorBrush(color) : null, StrokeThickness = width
        });
        switch (name)
        {
            case "star":
                // Orange star of the original's favorites.
                Draw("M8,1 L10.1,5.6 L15,6.1 L11.3,9.4 L12.4,14.5 L8,11.8 L3.6,14.5 L4.7,9.4 L1,6.1 L5.9,5.6 Z", "#FAB24A", true); break;
            case "history":
                // Traced from the original's «История» tab: a scroll with a curl at the top left, three lines of text
                // and a curl at the bottom right, with its light edge pixels.
                // F1: the rectangles overlap; the non-zero rule keeps the overlaps filled.
                Draw("F1 M0,1 h11 v1 h-11 Z M3,2 h9 v1 h-9 Z M0,1 h1 v5 h-1 Z M0,4 h5 v2 h-5 Z M3,2 h2 v12 h-2 Z M10,3 h1 v1 h-1 Z M11,3 h2 v9 h-2 Z M6,5 h4 v1 h-4 Z M6,8 h4 v1 h-4 Z M6,11 h4 v1 h-4 Z M5,13 h1 v1 h-1 Z M11,13 h1 v1 h-1 Z M4,14 h9 v1 h-9 Z M5,15 h11 v1 h-11 Z M11,11 h5 v2 h-5 Z M15,13 h1 v2 h-1 Z", "tab-glyph", true);
                Draw("M11,1 h1 v1 h-1 Z M12,2 h1 v1 h-1 Z M3,14 h1 v1 h-1 Z M4,15 h1 v1 h-1 Z", "tab-edge", true); break;
            case "shortcut":
                // Traced from the original's shortcut: a rounded tile with a blue frame and a light face, a dark blue
                // arrow with a sharp head pointing up and right.
                Draw("M2.5,1.5 H12.5 Q13.5,1.5 13.5,2.5 V12.5 Q13.5,13.5 12.5,13.5 H2.5 Q1.5,13.5 1.5,12.5 V2.5 Q1.5,1.5 2.5,1.5 Z", "#F1F1F8", true);
                Draw("M2.5,1.5 H12.5 Q13.5,1.5 13.5,2.5 V12.5 Q13.5,13.5 12.5,13.5 H2.5 Q1.5,13.5 1.5,12.5 V2.5 Q1.5,1.5 2.5,1.5 Z", "#3558C4", width: 1);
                Draw("M1.8,2 H13.2", "#6486EC", width: 1);
                Draw("M5.8,4 H10.6 V8.8 Z", "#2A4CB0", true);
                Draw("M8.6,6.4 L4.4,10.6", "#2A4CB0", width: 1.8); break;
            case "snapshots":
                // After the original's «Сохранённые снимки инф. баз» (ImgListButtons 9): a blue folder with a light edge of its
                // open flap and, in front of it, a sheet with a green header and two grey lines. All lines are straight,
                // so they fall on whole pixels and stay sharp when scaled.
                Draw("M2,3 h3 v1 h-3 Z M7,2 h6 v1 h-6 Z M6,3 h9 v1 h-9 Z M0,4 h16 v4 h-16 Z M0,8 h6 v7 h-6 Z", "#3E78B3", true);
                Draw("M2,5 h3 v1 h-3 Z M8,5 h4 v1 h-4 Z M7,6 h2 v2 h-2 Z", "#F0F4F9", true);
                Draw("M9,6 h5 v1 h-5 Z", "#BFD3E4", true);
                Draw("M8,10 h7 v5 h-7 Z", "field", true);
                Draw("M7,8 h9 v2 h-9 Z", "#55AC88", true);
                Draw("M7,10 h1 v6 h-1 Z M15,10 h1 v6 h-1 Z M7,15 h9 v1 h-9 Z", "#696969", true);
                Draw("M9,11 h5 v1 h-5 Z M9,13 h5 v1 h-5 Z", "#606060", true); break;
            case "settings":
                // Traced from the original gear: eight teeth around a ring with a round hole.
                Draw("M7,0 h2 v3 h-2 Z M7,13 h2 v3 h-2 Z M0,7 h3 v2 h-3 Z M13,7 h3 v2 h-3 Z", "#686868", true);
                Draw("M2,3.6 L3.6,2 L5.6,4 L4,5.6 Z M12.4,2 L14,3.6 L12,5.6 L10.4,4 Z M2,12.4 L4,10.4 L5.6,12 L3.6,14 Z M14,12.4 L12.4,14 L10.4,12 L12,10.4 Z", "#686868", true);
                Draw("M8,2.6 A5.4,5.4 0 1 1 7.99,2.6 Z M8,5.8 A2.2,2.2 0 1 0 8.01,5.8 Z", "#686868", true); break;
            case "chevron-down":
                // Traced from the original «Сервис» arrow: a thick dark grey chevron.
                Draw("M2.6,5 L6.5,8.9 L10.4,5", "#4F4F4F", width: 1.8); break;
            case "chevron-right": Draw("M5,3 L11,8 L5,13 Z", "#7F8790", true); break;
            case "folder":
                // Tan folder of the original's tree.
                Draw("M1,1 H6 L7,3 H15 V5 H1 Z", "#777777", true);
                Draw("M1,4 H15 V6 H1 Z", "#FFFFFF", true);
                Draw("M1,5 H15 V15 H1 Z", "#EAB765", true); break;
            // A folder with a green plus, as the original's «new group».
            case "folder-add":
                // Traced from the original: a folder with a dark line and a light strip under the tab, a large green circle
                // with a light rim and a thin white plus.
                Draw("M0,0 H7 L8,1 V2 H0 Z", "#EAB765", true);
                Draw("M0,2 h15 v1 h-15 Z", "#C4A26B", true);
                Draw("M0,3 h15 v1 h-15 Z", "#F0F0F0", true);
                Draw("M0,4 h15 v10 h-15 Z", "#EAB765", true);
                canvas.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Brush.Parse("#EDEDED"), [Canvas.LeftProperty] = 6.5, [Canvas.TopProperty] = 5.2 });
                canvas.Children.Add(new Ellipse { Width = 8.8, Height = 8.8, Fill = Brush.Parse("#4AA881"), [Canvas.LeftProperty] = 7.1, [Canvas.TopProperty] = 5.8 });
                Draw("M11.5,8 V13 M9,10.5 H14", "#FFFFFF", width: 1); break;
            case "favorite-folder":
                Draw("M1,1 H6 L7,3 H15 V5 H1 Z", "#777777", true);
                Draw("M1,4 H15 V6 H1 Z", "#FFFFFF", true);
                Draw("M1,5 H15 V15 H1 Z", "#EAB765", true);
                Draw("M8,6.8 L9.1,9.1 L11.6,9.4 L9.8,11.1 L10.2,13.6 L8,12.4 L5.8,13.6 L6.2,11.1 L4.4,9.4 L6.9,9.1 Z", "#FFFFFF", true); break;
            case "import":
                // Traced from the original: a clipboard open on the left, a grey clip, grey lines and a thick green chevron
                // arrow entering it.
                Draw("M2,2 h4 v1 h-4 Z M12,2 h4 v1 h-4 Z M2,3 h2 v1 h-2 Z M14,3 h2 v13 h-2 Z M2,14 h12 v2 h-12 Z", "#EAB766", true);
                Draw("M4,3 h10 v11 h-10 Z M2,13 h12 v1 h-12 Z", "#FFFFFF", true);
                Draw("M7,1 h4 v1 h-4 Z M7,2 h4 v1 h-4 Z M5,3 h8 v1 h-8 Z", "#787878", true);
                Draw("M7,6 h6 v1 h-6 Z M9,8 h4 v1 h-4 Z M8,10 h5 v1 h-5 Z M6,12 h7 v1 h-7 Z", "#B8B8B8", true);
                Draw("M2,5 H5 L9,9 L5,13 H2 L5,10 H0 V8 H5 Z", "#4BAD85", true); break;
            case "add":
                canvas.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = Brush.Parse("#4AA884") });
                Draw("M8,3.6 V12.4 M3.6,8 H12.4", "#FFFFFF", width: 2.3); break;
            case "edit":
                // Traced from the original «Свойства»: a brown table with a light header, cells and a blue circle with
                // a light «i».
                Draw("M1,1 h14 v2 h-14 Z", "#F3D29E", true);
                Draw("M1,7 h4 v1 h-4 Z M1,9 h4 v1 h-4 Z", "#F9ECD6", true);
                Draw("M0.5,0.5 H15.5 V6 M0.5,0.5 V14.5 H6", "#AB803A", width: 1);
                Draw("M0,3 h16 v1 h-16 Z M5,3 h1 v11 h-1 Z M0,6 h7 v1 h-7 Z M0,10 h5 v1 h-5 Z", "#AB803A", true);
                canvas.Children.Add(new Ellipse { Width = 9.8, Height = 9.8, Fill = Brush.Parse("#3E77B2"), [Canvas.LeftProperty] = 6.1, [Canvas.TopProperty] = 6.1 });
                Draw("M10,8 h2 v1 h-2 Z M10,11 h2 v3 h-2 Z", "#FFFFFF", true); break;
            case "delete":
                // Traced from the original: two bars at exactly 45°, so they cross at a right angle,
                // orange-red in the upper half and dark red below.
                Draw("M3.8,2.8 L12.8,11.8 M12.6,2.8 L3.6,11.8", "#F7806A", width: 2.5);
                Draw("M8.3,7.3 L12.8,11.8 M8.1,7.3 L3.6,11.8", "#D0473F", width: 2.5); break;
            case "search":
                // Grey magnifier of the original search field.
                Draw("M10.5,6.5 A4,4 0 1 1 2.5,6.5 A4,4 0 1 1 10.5,6.5 Z", "#707070", width: 1.6);
                Draw("M9.5,9.5 L14,14", "#707070", width: 2.2); break;
            case "duplicate":
                // Traced from the original «Дублировать информационную базу» (ImgListButtons 48): a green plus, a stepped
                // green block with a teal edge and a teal bar under it.
                Draw("M2,2 H4 V8 H2 Z M0,4 H6 V6 H0 Z", "#55A786", true);
                Draw("M7,4 H15 V11 H2 V9 H5 V7 H7 Z M2,12 H15 V13 H2 Z", "#4B9A91", true);
                Draw("M8,5 H15 V10 H5 V9 H6 V8 H7 V7 H8 Z", "#4BA881", true); break;
            case "clock":
                // Traced from the original status bar clock (StatusImage1): a dark ring, a white face and blue hands.
                Draw("M15,8 A7,7 0 1 1 1,8 A7,7 0 1 1 15,8 Z", "#FFFFFF", true);
                Draw("M15,8 A7,7 0 1 1 1,8 A7,7 0 1 1 15,8 Z", "#5A5A5A", width: 1.9);
                Draw("M7.5,3.5 V8.5 H13", "#2C6BAC", width: 1.1); break;
            case "search-connection":
                // Traced from the original «Поиск по строке соединения» (ImgListButtons 65): a dark slate magnifier and a letter C.
                Draw("M9.6,5.4 A4.1,4.1 0 1 1 1.4,5.4 A4.1,4.1 0 1 1 9.6,5.4 Z", "#3C5564", width: 1.4);
                Draw("M9.3,9.3 L12.6,12.6", "#3C5564", width: 1.9);
                Draw("M15.4,2.2 Q15,1.5 14,1.5 H13.4 Q12,1.5 12,3 V6 Q12,7.5 13.4,7.5 H14 Q15,7.5 15.4,6.8", "#3C5564", width: 1.3); break;
            case "close": Draw("M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5", "#303030", width: 1); break;
            case "play":
                Draw("M4,2 L13,8 L4,14 Z", "#2E9E6F", true); break;
            // Two green curved arrows, as the original's «reload the list».
            case "refresh":
                // Traced from the original's reload: a thick upper arrow with a thin tail going down on the left and its head
                // to the right, and the same arrow mirrored below.
                Draw("M9,2 H5.4 Q4.1,2.3 3.4,3.6 L2.2,6.8 L3.1,7.1 L4,5 Q4.6,4.4 5.6,4.4 H9 Z", "#10A924", true);
                Draw("M9,0.6 L13.2,4 L8.4,7.2 Z", "#10A924", true);
                Draw("M6,10.7 H10.5 Q11.3,10.5 11.8,9.6 L12.3,8.7 L13.1,9 L12.6,10.5 Q11.9,12.2 10,12.8 H6 Z", "#028C14", true);
                Draw("M7,8.6 L1.8,11.4 L6,15 Z", "#028C14", true); break;
            // A brush laid diagonally, as the original's «clear the temporary files» (red bristles: the rough clearing).
            case "clear":
            case "clear-hard":
                // Traced from the original's 16 px brush: a short dark handle in the corner with a light edge, a light gap,
                // a wide dark ferrule across the brush with a light edge towards the bristles, and one-colour bristles
                // with a rounded tip (red bristles: the rough clearing).
                Draw("M10.2,3.2 L13,0.6 L15.2,2.8 L12.5,5.5 Z", "#727272", true);
                Draw("M10.2,3.2 L13,0.6 L13.7,1.3 L10.9,3.9 Z", "#999999", true);
                Draw("M5.2,4.2 L8.8,2.8 L12.8,8.2 L10.2,10.2 Z", "#727272", true);
                Draw("M5.2,4.2 L10.2,10.2", "#B7B7B7", width: 1.1);
                Draw("M0,7 L3.2,5.4 Q3.9,5.1 4.5,5.6 L9.9,10.5 Q10.4,11.1 9.9,11.6 L6.9,14.6 Q5.9,15.5 5,14.8 L0.5,10.1 Q0,9.5 0,8.8 Z",
                    name == "clear" ? "#EAC282" : "#EE5A6A", true); break;
            case "designer":
                // Traced from the original configurator: a grey sheet at the back, a blue set square with a light cut-out,
                // blue and grey lines, a tan pencil and an orange ruler drawn as an outline with ticks.
                Draw("M3.5,2.5 V0.5 H14.5 V14.5 H13", "#727272", width: 1);
                Draw("M1,2 L9.5,10.5 V11 H1 Z", "#4A7DB1", true);
                Draw("M2.2,6.2 V9 H5 Z", "#DCE6EF", true);
                Draw("M5,2 h3 v1 h-3 Z", "#4A7DB1", true);
                Draw("M5,4 h8 v1 h-8 Z", "#B3B3B3", true);
                Draw("M10,6 h2 v5 h-2 Z", "#EAB96C", true);
                Draw("M1.5,12.5 H11.5 V15.5 H1.5 Z", "#E3751C", width: 1);
                Draw("M3,13 h1 v1 h-1 Z M5,13 h1 v1 h-1 Z M7,13 h1 v1 h-1 Z M9,13 h1 v1 h-1 Z", "#E3751C", true); break;
            // As on the original's toolbar: a tree trunk with green nodes and a white box with plus or minus.
            case "sort-asc":
            case "sort-desc":
            {
                // Traced from the original's FormSetVersion sort buttons: a blue A and a brown Z one above the other, a grey
                // arrow down on the right; the descending one has Z on top.
                var (a, z) = name == "sort-asc" ? (0d, 9d) : (8d, 0d);
                Draw(FormattableString.Invariant($"M1.5,{a + 7.5} L4.5,{a + .5} L7.5,{a + 7.5} M2.6,{a + 5} H6.4"), "#1630C8", width: 1.5);
                Draw(FormattableString.Invariant($"M1.5,{z + .5} H7 L1.5,{z + 6.5} H7.5"), "#A45F1D", width: 1.3);
                Draw("M11.5,0 V12", "#4F4F4F", width: 2);
                Draw("M8,10.5 L11.5,15.5 L15,10.5 Z", "#4F4F4F", true);
                break;
            }
            case "expand-all":
            case "collapse-all":
                // Traced from the original: green nodes with a highlight on a grey-blue trunk, and a light 9 px box with plus
                // or minus in front (the collapse icon has a second branch).
                Draw("M2.5,4 V16", "#63738C", width: 1);
                Draw(name == "expand-all" ? "M2.5,13.5 H5" : "M2.5,7.5 H5 M2.5,13.5 H5", "#63738C", width: 1);
                canvas.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Brush.Parse("#5AA563"), [Canvas.LeftProperty] = 0, [Canvas.TopProperty] = -0.4 });
                canvas.Children.Add(new Ellipse { Width = 1.6, Height = 1.6, Fill = Brush.Parse("#A5DE9C"), [Canvas.LeftProperty] = 1.7, [Canvas.TopProperty] = 0.8 });
                canvas.Children.Add(new Ellipse { Width = 4.8, Height = 4.8, Fill = Brush.Parse("#5AA563"), [Canvas.LeftProperty] = 4.9, [Canvas.TopProperty] = 4.7 });
                canvas.Children.Add(new Ellipse { Width = 4.4, Height = 4.4, Fill = Brush.Parse("#5AA563"), [Canvas.LeftProperty] = 3.8, [Canvas.TopProperty] = 11 });
                Draw("M7.5,7.5 H15.5 V15.5 H7.5 Z", "#F3F7FA", true);
                Draw("M8,11.5 H15 V15 H8 Z", "#E7E7E7", true);
                Draw("M7.5,7.5 H15.5 V15.5 H7.5 Z", "#848484", width: 1);
                Draw(name == "expand-all" ? "M9,11.5 H14 M11.5,9 V14" : "M9,11.5 H14", "#181818", width: 1); break;
            case "database":
                // Traced from the original's «1С:Предприятие 8» tab: list bars and a figure 8.
                Draw("M2,1 h12 v3 h-12 Z M2,6 h12 v1 h-12 Z M2,7 h7 v2 h-7 Z M2,11 h7 v3 h-7 Z M12,8 h3 v1 h-3 Z M11,9 h1 v2 h-1 Z M15,9 h1 v2 h-1 Z M12,10 h1 v1 h-1 Z M14,10 h1 v1 h-1 Z M12,11 h3 v1 h-3 Z M11,12 h1 v2 h-1 Z M15,12 h1 v2 h-1 Z M12,14 h3 v1 h-3 Z", "tab-glyph", true); break;
            case "server-base":
                // Traced from the original: a blue base plate with a tab down on the left and green letters SQL.
                Draw("M1,4 H14 V9 H4 V10 H3 V11 H1 Z", "#4A7DB1", true);
                Draw("M5,10 h1 v1 h-1 Z M6,10 h1 v1 h-1 Z M4,11 h1 v1 h-1 Z M5,12 h1 v1 h-1 Z M6,13 h1 v1 h-1 Z M4,14 h1 v1 h-1 Z M5,14 h1 v1 h-1 Z M6,14 h1 v1 h-1 Z M8,10 h1 v1 h-1 Z M9,10 h1 v1 h-1 Z M10,10 h1 v1 h-1 Z M7,11 h1 v1 h-1 Z M11,11 h1 v1 h-1 Z M7,12 h1 v1 h-1 Z M11,12 h1 v1 h-1 Z M7,13 h1 v1 h-1 Z M11,13 h1 v1 h-1 Z M8,14 h1 v1 h-1 Z M9,14 h1 v1 h-1 Z M10,14 h1 v1 h-1 Z M11,15 h1 v1 h-1 Z M12,10 h1 v1 h-1 Z M13,10 h1 v1 h-1 Z M13,11 h1 v1 h-1 Z M13,12 h1 v1 h-1 Z M13,13 h1 v1 h-1 Z M13,14 h1 v1 h-1 Z M14,14 h1 v1 h-1 Z M15,14 h1 v1 h-1 Z", "#397737", true); break;
            case "missing-base":
                // Traced from the original: a red base plate and a tan circle with a dark question mark in front.
                Draw("M1.5,1.5 H14.5 V7.5 H1.5 Z", "#FE0707", true);
                Draw("M1.5,7.5 V1.5 H14.5", "#CD0000", width: 1);
                canvas.Children.Add(new Ellipse { Width = 10.6, Height = 10.6, Fill = Brush.Parse("#EDC87E"), [Canvas.LeftProperty] = 5.2, [Canvas.TopProperty] = 5 });
                Draw("M8.8,8.8 Q9.4,7.3 10.9,7.3 Q12.9,7.4 12.8,9.1 Q12.6,10.3 11.3,11.2 Q10.8,11.7 10.8,12.6", "#404040", width: 1.4);
                Draw("M10,14 h2 v1 h-2 Z", "#404040", true); break;
            case "missing-folder":
                Draw("M1,1 H6 L7,3 H15 V5 H1 Z", "#777777", true);
                Draw("M1,4 H15 V6 H1 Z", "#FFFFFF", true);
                Draw("M1,5 H15 V15 H1 Z", "#FF5353", true); break;
            case "star-add":
                Draw("M6.5,1 L8.3,5 L12.5,5.4 L9.3,8.2 L10.2,12.5 L6.5,10.2 L2.8,12.5 L3.7,8.2 L0.5,5.4 L4.7,5 Z", "#FAB24A", true);
                Draw("M12.5,8.5 V15.5 M9,12 H16", "#51B81E", width: 2.4); break;
            case "rename":
                // Traced from the original: a blue text field with thick top and bottom edges and a text cursor across it
                // (the cursor uses the text colour, so it stays visible in the dark theme).
                Draw("M0,4 h15 v2 h-15 Z M0,10 h15 v2 h-15 Z M0,6 h1 v4 h-1 Z M14,6 h1 v4 h-1 Z", "#507996", true);
                Draw("M3,1 h5 v2 h-5 Z M5,3 h1 v10 h-1 Z M3,13 h5 v2 h-5 Z", "#303030", true); break;
            case "move":
                // A folder with a blue arrow, as the original's «move to group».
                Draw("M1,1 H6 L7,3 H15 V5 H1 Z", "#777777", true);
                Draw("M1,4 H15 V6 H1 Z", "#FFFFFF", true);
                Draw("M1,5 H15 V15 H1 Z", "#EAB765", true);
                Draw("M4,10.5 H11 M8.5,8 L11,10.5 L8.5,13", "#3E77B2", width: 1.8); break;
            case "copy":
                // Traced from the original «Копировать» (imgListService 8): an orange clipboard with a grey clip, a sheet
                // with lines and a green arrow pointing into it.
                Draw("M2,2 H5 V3 H4 V4 H2 Z M13,2 H16 V16 H2 V14 H14 V3 H13 Z", "#EAB766", true);
                Draw("M4,3 H14 V14 H2 V13 H4 Z", "#FFFFFF", true);
                Draw("M5,4 H13 V5 H5 Z", "#F4F4F4", true);
                Draw("M7.5,1 H10.5 V2 H11 V3 H13 V4 H5 V3 H7 V2 H7.5 Z", "#787878", true);
                Draw("M7.5,6 H13 V7 H7.5 Z M9.5,8 H13 V9 H9.5 Z M8.5,10 H13 V11 H8.5 Z M6.5,12 H13 V13 H6.5 Z", "#B8B8B8", true);
                Draw("M2,5 H5 L8,8 V10 L5,13 H2 L5,10 H0 V8 H5 Z", "#4BAD85", true); break;
            case "paste":
                // «Вставить» of the original: the same orange clipboard with a grey clip and a sheet with lines laid over it.
                Draw("M1,2 H12 V15 H1 Z", "#EAB766", true);
                Draw("M3,3 H10 V5 H3 Z", "#F4F4F4", true);
                Draw("M4.5,1 H8.5 V2 H9 V3 H10 V4 H3 V3 H4 V2 H4.5 Z", "#787878", true);
                Draw("M6,6 H15 V16 H6 Z", "#FFFFFF", true);
                Draw("M6.5,6.5 H14.5 V15.5 H6.5 Z", "#8A8A8A", width: 1);
                Draw("M8,9 H13 V10 H8 Z M8,11 H13 V12 H8 Z M8,13 H11.5 V14 H8 Z", "#B8B8B8", true); break;
            // The places of a new base, as the tiles of the original's «Размещение информационной базы»: a monitor, a database
            // cylinder, a globe.
            case "place-computer":
                Draw("M1.5,2.5 H14.5 V11.5 H1.5 Z", "#DCEBF7", true);
                Draw("M1.5,2.5 H14.5 V11.5 H1.5 Z", "#2F6EA5", width: 1);
                Draw("M4,5 H8 M4,7 H10 M4,9 H7", "#2F6EA5", width: 1);
                Draw("M6.5,12 h3 v2 h-3 Z M4,14 h8 v1.5 h-8 Z", "#5A5A5A", true); break;
            case "place-server":
                Draw("M3,3.5 C3,1.5 13,1.5 13,3.5 V12.5 C13,14.5 3,14.5 3,12.5 Z", "#F39A3B", true);
                Draw("M3,3.5 C3,5.5 13,5.5 13,3.5 M3,7 C3,9 13,9 13,7 M3,10.5 C3,12.5 13,12.5 13,10.5", "#B8620F", width: 1); break;
            case "place-web":
                canvas.Children.Add(new Ellipse { Width = 13, Height = 13, Fill = Brush.Parse("#DCEBF7"), Stroke = Brush.Parse("#3D7FC1"), StrokeThickness = 1,
                    [Canvas.LeftProperty] = 1.5, [Canvas.TopProperty] = 1.5 });
                Draw("M1.5,8 H14.5 M8,1.5 V14.5 M8,1.5 C4.5,4.5 4.5,11.5 8,14.5 M8,1.5 C11.5,4.5 11.5,11.5 8,14.5", "#3D7FC1", width: 1); break;
            // The arrow of the four ways and of «Создать», the curved arrow of «Назад», the pencil of an editable string.
            case "way":
                Draw("M2,8 H12.5 M8.5,4 L12.5,8 L8.5,12", "#3E77B2", width: 2); break;
            case "back":
                Draw("M5.5,3.5 L2,7 L5.5,10.5 M2,7 H10 C12.5,7 14,8.5 14,11 V13", "#3E77B2", width: 1.8); break;
            case "pencil":
                Draw("M10.5,2.5 L13.5,5.5 L5.5,13.5 L2,14 L2.5,10.5 Z", "#EAB765", true);
                Draw("M10.5,2.5 L13.5,5.5 L5.5,13.5 L2,14 L2.5,10.5 Z", "#8A6A2E", width: 1);
                Draw("M2,14 L2.3,11.8 L4.2,13.7 Z", "#303030", true); break;
            // The tree expander of the original: a small box with plus or minus.
            case "tree-plus":
            case "tree-minus":
                // 9 px box with a 7 px inside: the sign has an exact centre pixel and stays sharp at 16 px.
                Draw("M3.5,3.5 H11.5 V11.5 H3.5 Z", "#FFFFFF", true);
                Draw("M3.5,3.5 H11.5 V11.5 H3.5 Z", "#8C8C8C", width: 1);
                Draw(name == "tree-plus" ? "M5,7.5 H10 M7.5,5 V10" : "M5,7.5 H10", "#202020", width: 1); break;
            default:
                Draw("M6,1 H10 L11,4 L14,5 V10 L11,12 L10,15 H6 L5,12 L2,10 V5 L5,4 Z", "#7F8790");
                Draw("M11,8 A3,3 0 1 0 5,8 A3,3 0 1 0 11,8", "#7F8790"); break;
        }
        return canvas;
    }
    private static IBrush ColorBrush(string color) => color is "field" ? UiColors.Field
        : color is "tab-glyph" ? UiColors.TabGlyph : color is "tab-edge" ? UiColors.TabGlyphEdge
        : color is "#303030" ? UiColors.Text
        : color is "#686868" or "#737373" or "#4F4F4F" ? UiColors.Grey(color)
        : color is "#707B85" or "#7F8790" ? UiColors.Muted
        : color is "#397737" ? UiColors.BaseText
        : color is "#3C5564" ? UiColors.Slate
        : Brush.Parse(color);
}
