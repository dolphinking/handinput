﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.IO;

using Common.Logging;
using NDesk.Options;

using HandInput.Util;
using HandInput.Engine;
using Kinect.Toolbox.Record;
using chairgest = Aramis.Packages.KinectSDK.Services.Recorder;

namespace HandInput.OfflineProcessor {
  class Program {
    static readonly ILog Log = LogManager.GetCurrentClassLogger();

    static readonly String KinectPattern = "KinectData_*.bin";
    static readonly String GTPattern = "{0}DataGTD_*.txt";
    static readonly String KinectRegex = @"KinectData_(\d+).bin";
    static readonly String GTRegex = @"{0}DataGTD_(\d+).txt";
    static readonly String PidRegex = @"PID-0*([1-9a-z]+0*)$";
    static readonly String IndexRegex = @"(\d+)-?(\d+)?";
    static readonly String Ext = "csv";

    static readonly Int32 StartPid = 1, NumPids = 50;
    static readonly Int32 StartBatch = 1, NumBatches = 30;

    static readonly Dictionary<String, Type> Replayers = new Dictionary<String, Type>() {
      {"chairgest", typeof(chairgest.KinectReplay)}, {"standing", typeof(KinectAllFramesReplay)}
    };

    static readonly Dictionary<String, Type> HandTrackers = new Dictionary<String, Type> {
      {"salience", typeof(SalienceHandTracker)}, {"simple", typeof(SimpleSkeletonHandTracker)}
    };

    static readonly Dictionary<String, Type> FeatureProcessors = new Dictionary<String, Type> {
      {"hog", typeof(HogFeatureProcessor)}, {"simple", typeof(SimpleFeatureProcessor)}, 
      {"color", typeof(ColorFeatureProcessor)}
    };

    /// <summary>
    /// Default options.
    /// </summary>
    static int nSessions = 0;
    static float sampleRate = 1;
    static String type = "fe";
    static String sessionToProcess = null;
    static String gtSensor = "Kinect";
    static String dataName = "chairgest";
    static String handTrackerName = "salience";
    static String featureProcessorName = "simple";
    static bool keep = false;
    static int bufferSize = 1;
    static bool useParallel = false;

    static ParallelProcessor pp = new ParallelProcessor();
    static Object readLock = new Object();
    static Object writeLock = new Object();
    static IEnumerable<Int32> pidList, batchList;

    static Type replayerType;
    static Type handTrackerType;
    static Type featureProcessorType;

    public static void Main(string[] args) {
      String inputFolder = null;
      String outputFolder = null;
      bool showHelp = false;
      pidList = Enumerable.Range(StartPid, NumPids);
      batchList = Enumerable.Range(StartBatch, NumBatches);

      var p = new OptionSet() {
        { "i=", "the input {FOLDER} of the data set", v => inputFolder = v },
        { "o=", "the output {FOLDER} of the processed data", v => outputFolder = v },
        { "t|type=", String.Format("type of the operation: gt|fe. [{0}]", type), 
            v => type = v },
        { "p=", "the {PID} of the data set to process. Can be a single number or a " +
            "range like 1-17. Default is all.", v => pidList = ParseIndex(v) },
        { "b=", "the {BATCH NUMBER(S)} to process. Can be a single number or a range like 1-8." +  
            " Default is all.", v => batchList = ParseIndex(v) },
        { "h|help", "show this message and exit", v => showHelp = v != null },
        { "s=", "{SESSION} name to be processed", v => sessionToProcess = v },
        { "ns=", String.Format("{{NUMBER OF SESSIONS}} to be processed. If 0, process all sessions. [{0}]",
                               nSessions),
            v => nSessions = Int32.Parse(v) },
        { "gs=", String.Format("{{SENSOR}} for ground truth. [{0}]", gtSensor), 
            v => gtSensor = v},
        { "sample=", String.Format("{{SAMPLE RATE}}. [{0}]", sampleRate), 
            v => sampleRate = Single.Parse(v) },
        { "data=", String.Format("{{DATA TYPE}}: {0}. [{1}]", 
                                 ToString(Replayers.Keys), dataName),
            v => dataName = v },
        { "tracker=", String.Format("{{HAND TRACKER TYPE}}: {0}. [{1}]", 
                                    ToString(HandTrackers.Keys), handTrackerName),
            v => handTrackerName = v },
        { "processor=", String.Format("{{FEATURE PROCESSOR TYPE}}: {0}. [{1}]", 
                                      ToString(FeatureProcessors.Keys), featureProcessorName),
            v => featureProcessorName = v },
        { "k|keep", "keep the exisiting processed file if present.", v => keep = v != null },
        { "buffer=", String.Format("{{BUFFER SIZE}}. [{0}]", bufferSize),
            v => bufferSize = Int32.Parse(v) },
        { "parallel", String.Format("Use parallel processing if present. [{0}]", useParallel),
          v => useParallel = v != null}
      };

      try {
        p.Parse(args);
      } catch (OptionException oe) {
        Console.WriteLine(oe.Message);
        return;
      }

      if (showHelp) {
        p.WriteOptionDescriptions(Console.Out);
        return;
      }

      ValidateOptions();

      Log.InfoFormat("Use parallel: {0}", useParallel);

      var stopWatch = new Stopwatch();
      stopWatch.Start();
      ProcessPids(inputFolder, outputFolder);
      stopWatch.Stop();
      var ts = stopWatch.Elapsed;
      var elapsedTime = String.Format("{0:00}:{1:00}", ts.Hours, ts.Minutes);
      Log.InfoFormat("Run tinme = {0}", elapsedTime);
    }

    static void ValidateOptions() {
      var found = Replayers.TryGetValue(dataName, out replayerType);
      if (!found) {
        ErrorExit("Invalid data type name.");
      }

      found = HandTrackers.TryGetValue(handTrackerName, out handTrackerType);
      if (!found) {
        ErrorExit("Invalid hand tracker name.");
      }

      found = FeatureProcessors.TryGetValue(featureProcessorName, out featureProcessorType);
      if (!found) {
        ErrorExit("Invalid feature processor name.");
      }
    }

    static String ToString(IEnumerable<String> collection) {
      return String.Join(",", collection);
    }

    static void ErrorExit(String message) {
      Console.Error.WriteLine(message);
      Environment.Exit(-1);
    }

    static IEnumerable<Int32> ParseIndex(String index) {
      var match = Regex.Match(index, IndexRegex);

      if (match.Success) {
        var startNdx = Int32.Parse(match.Groups[1].Value);
        var endNdx = startNdx;
        if (match.Groups.Count > 2 && match.Groups[2].Length > 0)
          endNdx = Int32.Parse(match.Groups[2].Value);
        return Enumerable.Range(startNdx, endNdx - startNdx + 1);
      } else {
        throw new OptionException("Cannot parse index option.", index);
      }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="inputFolder">Main database folder.</param>
    /// <param name="outputFolder"></param>
    private static void ProcessPids(String inputFolder, String outputFolder) {
      var dirs = Directory.GetDirectories(inputFolder);
      foreach (var dir in dirs) {
        var dirInfo = new DirectoryInfo(dir);
        var match = Regex.Match(dirInfo.Name, PidRegex);
        if (match.Success) {
          var s = match.Groups[1].Value;
          int pid;
          var isInt = Int32.TryParse(s, out pid);
          if (!isInt || pidList.Contains(pid)) {
            var outputPidDir = Path.Combine(outputFolder, dirInfo.Name);
            ProcessSessions(dir, outputPidDir);
          }
        }
      }
      if (type.Equals("fe"))
        pp.WaitAll();
    }

    private static void ProcessSessions(String inputFolder, String outputFolder) {
      String[] sessionDirs = null;

      if (sessionToProcess != null) {
        sessionDirs = new String[] { sessionToProcess };
      } else {
        sessionDirs = Directory.GetDirectories(inputFolder);
      }

      var nSessionsToProcess = nSessions;
      if (nSessionsToProcess == 0) {
        nSessionsToProcess = sessionDirs.Count();
      }

      foreach (var dir in sessionDirs.Take(nSessionsToProcess)) {
        var dirInfo = new DirectoryInfo(dir);
        var inputSession = Path.Combine(inputFolder, dirInfo.Name);
        var outputSession = Path.Combine(outputFolder, dirInfo.Name);
        // Create all directories specified in the path unless they already exist.
        Directory.CreateDirectory(outputSession);
        ProcessBatches(inputSession, outputSession);
      }
    }

    /// <summary>
    /// Process all the batch data in one session.
    /// </summary>
    /// <param name="inputSessionFolder"></param>
    /// <param name="outputSessionFolder"></param>
    private static void ProcessBatches(String inputSessionFolder, String outputSessionFolder) {
      Log.DebugFormat("Process session {0}:", inputSessionFolder);

      var inputPattern = KinectPattern;
      var regex = KinectRegex;
      if (type.Equals("gt")) {
        inputPattern = String.Format(GTPattern, gtSensor);
        regex = String.Format(GTRegex, gtSensor);
      }

      String[] filePaths = Directory.GetFiles(inputSessionFolder, inputPattern);

      foreach (var inFile in filePaths) {
        var fileInfo = new FileInfo(inFile);
        var name = fileInfo.Name;

        var match = Regex.Match(name, regex);
        if (match.Success) {
          int batchNum = Int32.Parse(match.Groups[1].Value);
          if (batchList.Contains(batchNum)) {
            if (type.Equals("gt")) {
              var outputFile = Path.Combine(outputSessionFolder, name);
              File.Copy(inFile, outputFile, true);
            } else {
              var outFile = Path.Combine(outputSessionFolder, Path.ChangeExtension(name, Ext));

              if (File.Exists(outFile) && keep)
                continue;

              OfflineProcessor proc = new OfflineProcessor(inFile, outFile, readLock, writeLock,
                  replayerType, handTrackerType, featureProcessorType, sampleRate, gtSensor,
                  bufferSize);
              try {
                if (useParallel)
                  pp.Spawn(proc.Process);
                else
                  proc.Process();
              } catch (Exception ex) {
                Log.Error(ex.Message);
              }
            }
          }
        }
      }
    }
  }
}
