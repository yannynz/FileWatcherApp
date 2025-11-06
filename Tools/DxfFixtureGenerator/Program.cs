using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Tables;

namespace DxfFixtureGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        var outputRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "resources", "dxf"));
        Directory.CreateDirectory(outputRoot);

        GenerateSerrilhaFina(Path.Combine(outputRoot, "serrilha_fina.dxf"));
        GenerateSerrilhaMista(Path.Combine(outputRoot, "serrilha_mista.dxf"));
        GenerateSerrilhaNaoMapeada(Path.Combine(outputRoot, "serrilha_nao_map.dxf"));
        GenerateLowComplexity(Path.Combine(outputRoot, "calibration_low_complexity.dxf"));
        GenerateZipperComplexity(Path.Combine(outputRoot, "calibration_zipper_complexity.dxf"));
        GenerateThreePtComplexity(Path.Combine(outputRoot, "calibration_threept_complexity.dxf"));

        Console.WriteLine($"Fixtures generated under {outputRoot}");
        return 0;
    }

    private static void GenerateSerrilhaFina(string path)
    {
        var doc = CreateDocument();
        var layer = new Layer("SERRILHA_SIMBOLOS");
        doc.Layers.Add(layer);

        var block = new Block("SERRILHA_FINA");
        block.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(20, 0, 0)));
        block.Entities.Add(new Line(new netDxf.Vector3(5, 0, 0), new netDxf.Vector3(10, 5, 0)));
        block.Entities.Add(new Line(new netDxf.Vector3(10, 5, 0), new netDxf.Vector3(15, 0, 0)));
        doc.Blocks.Add(block);

        var insert = new Insert(block)
        {
            Layer = layer,
            Position = new netDxf.Vector3(10, 10, 0)
        };

        doc.Entities.Add(insert);
        doc.Save(path);
    }

    private static void GenerateSerrilhaMista(string path)
    {
        var doc = CreateDocument();

        var serrilhaLayer = new Layer("SERRILHA_MISTA");
        var corteLayer = new Layer("CUT");
        doc.Layers.Add(serrilhaLayer);
        doc.Layers.Add(corteLayer);

        var block = new Block("SERRILHA_MIX");
        block.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(0, 0)),
            new Polyline2DVertex(new netDxf.Vector2(10, 5)),
            new Polyline2DVertex(new netDxf.Vector2(20, 0)),
            new Polyline2DVertex(new netDxf.Vector2(30, 5)),
            new Polyline2DVertex(new netDxf.Vector2(40, 0))
        }, false));
        doc.Blocks.Add(block);

        for (var i = 0; i < 3; i++)
        {
            doc.Entities.Add(new Insert(block)
            {
                Layer = serrilhaLayer,
                Position = new netDxf.Vector3(40 * i, 0, 0),
                Rotation = i * 15
            });
        }

        doc.Entities.Add(new Circle(new netDxf.Vector3(60, 25, 0), 12) { Layer = corteLayer });
        doc.Entities.Add(new Circle(new netDxf.Vector3(110, 25, 0), 10) { Layer = corteLayer });

        doc.Save(path);
    }

    private static void GenerateSerrilhaNaoMapeada(string path)
    {
        var doc = CreateDocument();
        var layer = new Layer("SERRILHA_DESCONHECIDA");
        doc.Layers.Add(layer);

        var block = new Block("SERRILHA_UNKNOWN");
        block.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(20, 0, 0)));
        block.Entities.Add(new Arc(new netDxf.Vector3(10, 5, 0), 6, 0, 180));
        block.Entities.Add(new Line(new netDxf.Vector3(20, 0, 0), new netDxf.Vector3(30, 5, 0)));
        doc.Blocks.Add(block);

        doc.Entities.Add(new Insert(block)
        {
            Layer = layer,
            Position = new netDxf.Vector3(5, 5, 0)
        });

        doc.Entities.Add(new Insert(block)
        {
            Layer = layer,
            Position = new netDxf.Vector3(45, 5, 0),
            Rotation = 45
        });

        doc.Save(path);
    }

    private static void GenerateLowComplexity(string path)
    {
        var doc = CreateDocument();

        var cutLayer = new Layer("CUT");
        var adhesiveLayer = new Layer("ADESIVO");
        doc.Layers.Add(cutLayer);
        doc.Layers.Add(adhesiveLayer);

        var rectangle = new[]
        {
            new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(400, 0, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(400, 0, 0), new netDxf.Vector3(400, 280, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(400, 280, 0), new netDxf.Vector3(0, 280, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(0, 280, 0), new netDxf.Vector3(0, 0, 0)) { Layer = cutLayer }
        };

        foreach (var line in rectangle)
        {
            doc.Entities.Add(line);
        }

        doc.Entities.Add(new Circle(new netDxf.Vector3(90, 70, 0), 12) { Layer = cutLayer });
        doc.Entities.Add(new Circle(new netDxf.Vector3(310, 210, 0), 12) { Layer = cutLayer });

        doc.Entities.Add(new Line(new netDxf.Vector3(0, 140, 0), new netDxf.Vector3(400, 140, 0)) { Layer = cutLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(200, 0, 0), new netDxf.Vector3(200, 280, 0)) { Layer = cutLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(0, 70, 0), new netDxf.Vector3(400, 70, 0)) { Layer = cutLayer });

        doc.Entities.Add(new Arc(new netDxf.Vector3(320, 60, 0), 8, 0, 210) { Layer = cutLayer });

        doc.Entities.Add(new Line(new netDxf.Vector3(150, 30, 0), new netDxf.Vector3(250, 30, 0)) { Layer = adhesiveLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(250, 30, 0), new netDxf.Vector3(250, 90, 0)) { Layer = adhesiveLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(250, 90, 0), new netDxf.Vector3(150, 90, 0)) { Layer = adhesiveLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(150, 90, 0), new netDxf.Vector3(150, 30, 0)) { Layer = adhesiveLayer });

        doc.Save(path);
    }

    private static void GenerateZipperComplexity(string path)
    {
        var doc = CreateDocument();

        var cutLayer = new Layer("CUT");
        var zipperLayer = new Layer("ZIPPER");
        var adhesiveLayer = new Layer("ADESIVO");

        doc.Layers.Add(cutLayer);
        doc.Layers.Add(zipperLayer);
        doc.Layers.Add(adhesiveLayer);

        foreach (var entity in new EntityObject[]
        {
            new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(280, 0, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(280, 0, 0), new netDxf.Vector3(280, 150, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(280, 150, 0), new netDxf.Vector3(0, 150, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(0, 150, 0), new netDxf.Vector3(0, 0, 0)) { Layer = cutLayer }
        })
        {
            doc.Entities.Add(entity);
        }

        for (var i = 0; i < 12; i++)
        {
            var x = 20 + i * 20;
            doc.Entities.Add(new Circle(new netDxf.Vector3(x, 30, 0), 6) { Layer = cutLayer });
        }

        var zipperBlock = new Block("SERRILHA_ZIPPER");
        zipperBlock.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(0, 0), 0),
            new Polyline2DVertex(new netDxf.Vector2(15, 4), 0),
            new Polyline2DVertex(new netDxf.Vector2(30, 0), 0)
        }, true));
        doc.Blocks.Add(zipperBlock);

        doc.Entities.Add(new Insert(zipperBlock)
        {
            Layer = zipperLayer,
            Position = new netDxf.Vector3(40, 90, 0)
        });

        doc.Entities.Add(new Insert(zipperBlock)
        {
            Layer = zipperLayer,
            Position = new netDxf.Vector3(180, 90, 0)
        });

        doc.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(20, 120), 0),
            new Polyline2DVertex(new netDxf.Vector2(120, 120), 0),
            new Polyline2DVertex(new netDxf.Vector2(120, 140), 0),
            new Polyline2DVertex(new netDxf.Vector2(20, 140), 0)
        }, true) { Layer = adhesiveLayer });

        doc.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(160, 120), 0),
            new Polyline2DVertex(new netDxf.Vector2(260, 120), 0),
            new Polyline2DVertex(new netDxf.Vector2(260, 140), 0),
            new Polyline2DVertex(new netDxf.Vector2(160, 140), 0)
        }, true) { Layer = adhesiveLayer });

        doc.Entities.Add(new Arc(new netDxf.Vector3(60, 60, 0), 12, 0, 270) { Layer = cutLayer });
        doc.Entities.Add(new Arc(new netDxf.Vector3(220, 55, 0), 10, 90, 360) { Layer = cutLayer });

        doc.Save(path);
    }

    private static void GenerateThreePtComplexity(string path)
    {
        var doc = CreateDocument();

        var cutLayer = new Layer("CUT");
        var threePtLayer = new Layer("VINCO_3PT");
        var specialLayer = new Layer("MATERIAL_ESPECIAL");
        doc.Layers.Add(cutLayer);
        doc.Layers.Add(threePtLayer);
        doc.Layers.Add(specialLayer);

        var outline = new[]
        {
            new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(360, 0, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(360, 0, 0), new netDxf.Vector3(360, 260, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(360, 260, 0), new netDxf.Vector3(0, 260, 0)) { Layer = cutLayer },
            new Line(new netDxf.Vector3(0, 260, 0), new netDxf.Vector3(0, 0, 0)) { Layer = cutLayer }
        };

        foreach (var line in outline)
        {
            doc.Entities.Add(line);
        }

        AddThreePointPath(doc, threePtLayer, new netDxf.Vector3(60, 40, 0), width: 220, segments: 5);
        AddThreePointPath(doc, threePtLayer, new netDxf.Vector3(60, 150, 0), width: 220, segments: 3, inverted: true);

        doc.Entities.Add(new Circle(new netDxf.Vector3(80, 80, 0), 10) { Layer = cutLayer });
        doc.Entities.Add(new Circle(new netDxf.Vector3(280, 180, 0), 14) { Layer = cutLayer });

        doc.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(40, 200), 0),
            new Polyline2DVertex(new netDxf.Vector2(140, 200), 0),
            new Polyline2DVertex(new netDxf.Vector2(140, 240), 0),
            new Polyline2DVertex(new netDxf.Vector2(40, 240), 0)
        }, true) { Layer = specialLayer });

        doc.Entities.Add(new Polyline2D(new[]
        {
            new Polyline2DVertex(new netDxf.Vector2(200, 30), 0),
            new Polyline2DVertex(new netDxf.Vector2(300, 30), 0),
            new Polyline2DVertex(new netDxf.Vector2(300, 70), 0),
            new Polyline2DVertex(new netDxf.Vector2(200, 70), 0)
        }, true) { Layer = specialLayer });

        doc.Save(path);
    }

    private static void AddThreePointPath(DxfDocument doc, Layer layer, netDxf.Vector3 origin, double width, int segments, bool inverted = false)
    {
        var segmentWidth = width / segments;
        var yOffset = inverted ? -8 : 8;
        var direction = inverted ? -1 : 1;

        for (var i = 0; i < segments; i++)
        {
            var startX = origin.X + (segmentWidth * i);
            var endX = startX + segmentWidth;

            var top = new Line(
                new netDxf.Vector3(startX, origin.Y, 0),
                new netDxf.Vector3(endX, origin.Y, 0))
            { Layer = layer };

            var offset = new Line(
                new netDxf.Vector3(startX, origin.Y + direction * yOffset, 0),
                new netDxf.Vector3(endX, origin.Y + direction * yOffset, 0))
            { Layer = layer };

            doc.Entities.Add(top);
            doc.Entities.Add(offset);

            doc.Entities.Add(new Line(
                top.StartPoint,
                offset.StartPoint) { Layer = layer });

            doc.Entities.Add(new Line(
                top.EndPoint,
                offset.EndPoint) { Layer = layer });
        }
    }

    private static DxfDocument CreateDocument()
    {
        var doc = new DxfDocument(DxfVersion.AutoCad2010)
        {
            Name = "Fixture"
        };

        doc.DrawingVariables.InsUnits = netDxf.Units.DrawingUnits.Millimeters;
        return doc;
    }
}
