using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WifiScanner.EditorTests
{
    /// <summary>Dependency-free edit-mode regression suite. Run from
    /// Tools/WiFi Scanner/Run Survey Regression Tests or with Unity's
    /// -executeMethod WifiScanner.EditorTests.SurveyAnalyzerRegressionTests.RunAll.
    /// An exception makes batch mode fail immediately.</summary>
    public static class SurveyAnalyzerRegressionTests
    {
        private static readonly long Now =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [MenuItem("Tools/WiFi Scanner/Run Survey Regression Tests")]
        public static void RunAll()
        {
            RunCore();
            Debug.Log("[SurveyTests] 12 regression tests passed.");
        }

        public static void RunCore()
        {
            LegacySample();
            WelfordFolding();
            SingleMeasurement();
            HighVariance();
            DenseStable();
            OldData();
            AlignmentQuality();
            RoomBeyondMeasuredBounds();
            MultipleFloors();
            SeparateBssidsDoNotCreateVariance();
            AlignedGridUsesHeatmapYaw();
            UnwalkableCellIsExcluded();
        }

        private static void LegacySample()
        {
            var s = new Sample { timestampMs=Now, rssiDbm=-60 };
            s.NormalizeLegacyMetadata();
            Require(s.sampleCount == 1 && !s.HasRssiVariance &&
                    s.trackingQuality == -1f && s.alignmentQuality == -1f,
                "legacy metadata normalization");
        }

        private static void WelfordFolding()
        {
            var s=Point(0,0,1,0); s.FoldRssi(-50,Now+1); s.FoldRssi(-70,Now+2);
            Require(s.sampleCount==3 && Mathf.Abs(s.rssiMeanDbm+60f)<.001f &&
                    Mathf.Abs(s.RssiVariance-100f)<.001f, "Welford folding");
        }

        private static void SingleMeasurement()
        {
            var r=Build(new[]{Point(.25f,.25f,1,0)});
            Require(r.cells[0].status==SurveyAnalyzer.SurveyCellStatus.Measured &&
                    r.cells[0].quality.rssiVariance<0f, "single measurement");
        }

        private static void HighVariance()
        {
            var r=Build(new[]{Point(.25f,.25f,3,200)});
            Require(r.cells[0].status==SurveyAnalyzer.SurveyCellStatus.Critical,
                "high variance");
        }

        private static void DenseStable()
        {
            var r=Build(new[]{Point(.25f,.25f,4,2)});
            Require(r.cells[0].status==SurveyAnalyzer.SurveyCellStatus.Sufficient &&
                    r.cells[0].quality.score>60f, "dense stable samples");
        }

        private static void OldData()
        {
            var s=Point(.25f,.25f,4,2);s.lastTimestampMs=Now-7200000;
            Require(Build(new[]{s}).cells[0].quality.stale, "stale data");
        }

        private static void AlignmentQuality()
        {
            var low=Point(.25f,.25f,4,2);low.alignmentQuality=.2f;
            var high=low;high.alignmentQuality=.95f;
            var a=Build(new[]{low}).cells[0].quality;
            var b=Build(new[]{high}).cells[0].quality;
            Require(b.score>a.score && b.trackingScore<0f,
                "alignment without invented tracking quality");
        }

        private static void RoomBeyondMeasuredBounds()
        {
            var r=SurveyAnalyzer.BuildSurvey(new[]{Point(.25f,.25f,3,1)},
                new[]{Square(0,0,2)},0,"EG",0,Config(),null,null,Now);
            Require(r.totalCells>1 && r.openCells>0, "unvisited room bounds");
        }

        private static void MultipleFloors()
        {
            var samples=new[]{Point(.25f,.25f,3,1,0),Point(.25f,.25f,3,1,1,3)};
            var rooms=new[]{Square(0,0,.5f),Square(1,3,.5f)};
            var a=SurveyAnalyzer.BuildSurvey(samples,rooms,0,"EG",0,Config(),null,null,Now);
            var b=SurveyAnalyzer.BuildSurvey(samples,rooms,1,"OG1",3,Config(),null,null,Now);
            Require(a.totalCells==1 && b.totalCells==1 && b.cells[0].floorLabel=="OG1",
                "multiple floors");
        }

        private static void SeparateBssidsDoNotCreateVariance()
        {
            var a=Point(.25f,.25f,4,2); a.bssid="A"; a.rssiDbm=-48;
            a.rssiMeanDbm=-48;
            var b=Point(.25f,.25f,4,2); b.bssid="B"; b.rssiDbm=-82;
            b.rssiMeanDbm=-82;
            var r=Build(new[]{a,b});
            Require(r.cells[0].status==SurveyAnalyzer.SurveyCellStatus.Sufficient &&
                    r.cells[0].quality.rssiStdDev<5f,
                "separate BSSIDs are not RSSI variance");
        }

        private static void AlignedGridUsesHeatmapYaw()
        {
            const float yaw=31f;
            var r=SurveyAnalyzer.BuildSurvey(new[]{Point(.25f,.25f,4,2)},
                new[]{Square(0,0,2)},0,"EG",0,Config(),null,null,Now,yaw);
            Require(r.cells.Count>0 && Mathf.Abs(r.gridYawDegrees-yaw)<.001f,
                "aligned grid yaw stored");
            var c=r.cells[0];
            double radians=yaw*(Math.PI/180.0);
            float wx=(float)(c.gridX*Math.Cos(radians)+c.gridZ*Math.Sin(radians));
            float wz=(float)(-c.gridX*Math.Sin(radians)+c.gridZ*Math.Cos(radians));
            Require(Mathf.Abs(wx-c.centerX)<.001f &&
                    Mathf.Abs(wz-c.centerZ)<.001f,
                "aligned cell coordinates match heatmap transform");
        }

        private static void UnwalkableCellIsExcluded()
        {
            var samples=new[]{Point(.25f,.25f,4,2),Point(.75f,.25f,4,2),
                Point(.25f,.75f,4,2),Point(.75f,.75f,4,2)};
            var rooms=new[]{Square(0,0,1f)};
            var first=SurveyAnalyzer.BuildSurvey(samples,rooms,0,"EG",0,
                Config(),null,null,Now);
            var excluded=new HashSet<string>{first.cells[0].id};
            var second=SurveyAnalyzer.BuildSurvey(samples,rooms,0,"EG",0,
                Config(),excluded,null,Now);
            Require(second.cells[0].status==SurveyAnalyzer.SurveyCellStatus.Unwalkable &&
                    second.openCells==0 && second.valid,
                "unwalkable cell excluded from completion");
        }

        private static SurveyAnalyzer.SurveyFloorResult Build(Sample[] samples) =>
            SurveyAnalyzer.BuildSurvey(samples,new[]{Square(0,0,.5f)},0,"EG",0,
                Config(),null,null,Now);

        private static SurveyAnalyzer.QualityConfig Config() => new()
        { cellSize=.5f,measurementRadius=.75f,minimumEffectiveMeasurements=3,
          maximumRssiStdDev=5f,staleAfterMinutes=60f,targetMeasurementSpacing=.5f };

        private static Sample Point(float x,float z,int count,float m2,
            int floor=0,float y=0) => new()
        { timestampMs=Now,firstTimestampMs=Now,lastTimestampMs=Now,posX=x,posY=y,
          posZ=z,rssiDbm=-60,rssiMeanDbm=-60,rssiM2=m2,sampleCount=count,
          floor=floor,trackingQuality=-1f,alignmentQuality=-1f };

        private static RoomOutline Square(int floor,float y,float size) => new()
        { floorY=y,name="R"+floor,height=2.5f,cornersXZ=new List<Vector2>
          {Vector2.zero,new(size,0),new(size,size),new(0,size)} };

        private static void Require(bool condition,string name)
        {
            if (!condition) throw new InvalidOperationException(
                "Survey regression failed: "+name);
        }
    }
}
