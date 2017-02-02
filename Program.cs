using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.CommandLineUtils;


namespace StreamCapture
{
    
    public class Program
    {
        public static void Main(string[] args)
        {
            CommandLineApplication commandLineApplication = new CommandLineApplication(throwOnUnexpectedArg: false);
            CommandOption channels = commandLineApplication.Option("-c | --channels","Channels to record in the format nn+nn+nn (must be 2 digits)",CommandOptionType.SingleValue);
            CommandOption duration = commandLineApplication.Option("-d | --duration","Duration in minutes to record",CommandOptionType.SingleValue);
            CommandOption filename = commandLineApplication.Option("-f | --filename","File name (no extension)",CommandOptionType.SingleValue);
            CommandOption datetime = commandLineApplication.Option("-d | --datetime","Datetime MM/DD/YY HH:MM (optional)",CommandOptionType.SingleValue);
            commandLineApplication.HelpOption("-? | -h | --help");
            commandLineApplication.Execute(args);     
            
            //do we have optional args passed in?
            bool optionalArgsFlag=false;
            if(channels.HasValue() && duration.HasValue() && filename.HasValue())
                optionalArgsFlag=true;
            else
                Console.WriteLine($"{DateTime.Now}: No usable command line options passed in - Using keywords to search schedule. (Please run with --help if you'ref confused)");

            //Read and build config
            var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
            var configuration = builder.Build();

            //Read channel history (used to determine which order to use)
            ChannelHistory channelHistory = new ChannelHistory();

            //Use optional parameters if passed in
            if(optionalArgsFlag)
            {
                //Create new RecordInfo
                RecordInfo recordInfo = new RecordInfo(channelHistory);
                recordInfo.channels.LoadChannels(channels.Value());
                recordInfo.strDuration=duration.Value();
                recordInfo.strStartDT=datetime.Value();
                recordInfo.fileName=filename.Value();

                //Start recording
                Program p = new Program();
                p.MainAsync(recordInfo,configuration).Wait();
                Environment.Exit(0);
            }

            //Since we'ref using keywords, grab schedule from interwebs and loop forever, checking every n hours for new shows to record
            while(true)
            {
                //Get keywords
                Keywords keywords = new Keywords();

                //Get latest schedule
                Schedule schedule=new Schedule();
                schedule.LoadSchedule().Wait();
                Dictionary<string,RecordInfo> recordSched=schedule.GetRecordSchedule(keywords,configuration,channelHistory);

                //Spawn new process for each show found
                foreach (KeyValuePair<string, RecordInfo> kvp in recordSched)
                {            
                    RecordInfo recordInfo = (RecordInfo)kvp.Value;

                    //If show is not already in the past or waiting, get that done
                    int hoursInFuture=Convert.ToInt32(configuration["hoursInFuture"]);
                    if(recordInfo.GetStartDT()>DateTime.Now && recordInfo.GetStartDT()<=DateTime.Now.AddHours(hoursInFuture) && !recordInfo.processSpawnedFlag)
                    {
                        recordInfo.processSpawnedFlag=true;
                        DumpRecordInfo(Console.Out,recordInfo,"Schedule Read: "); 
        
                        Program p = new Program();
                        Task.Factory.StartNew(() => p.MainAsync(recordInfo,configuration));  //use threadpool instead of more costly os threads
                    }
                    else
                    {
                        Console.WriteLine($"{DateTime.Now}: ignoring id: {recordInfo.id}");
                    }
                }  

                //Determine how long to sleep before next check
                string[] times=configuration["scheduleCheck"].Split(',');
                DateTime nextRecord=DateTime.Now;
                
                //find out if today
                if(DateTime.Now.Hour < Convert.ToInt32(times[times.Length-1]))
                {
                    for(int i=0;i<times.Length;i++)
                    {
                        int recHour=Convert.ToInt32(times[i]);
                        if(DateTime.Now.Hour < recHour)
                        {
                            nextRecord=new DateTime(DateTime.Now.Year,DateTime.Now.Month,DateTime.Now.Day,recHour,0,0,0,DateTime.Now.Kind);
                            break;
                        }
                    }
                }
                else
                {
                    //build for tomorrow
                    int recHour=Convert.ToInt32(times[0]);  //grab first time
                    nextRecord=new DateTime(DateTime.Now.Year,DateTime.Now.Month,DateTime.Now.Day+1,recHour,0,0,0,DateTime.Now.Kind);
                }

                //Wait
                TimeSpan timeToWait = nextRecord - DateTime.Now;
                Console.WriteLine($"{DateTime.Now}: Now sleeping for {timeToWait.Hours+1} hours before checking again at {nextRecord.ToString()}");
                Thread.Sleep(timeToWait);         
                Console.WriteLine($"{DateTime.Now}: Woke up, now checking again...");
            } 
        }

        private static void DumpRecordInfo(TextWriter logWriter,RecordInfo recordInfo,string tag)
        {
            logWriter.WriteLine($"{tag} =====================");
            logWriter.WriteLine($"Show: {recordInfo.description} StartDT: {recordInfo.GetStartDT()}  Duration: {recordInfo.GetDuration()}");
            logWriter.WriteLine($"File: {recordInfo.fileName}");
            logWriter.WriteLine($"Channels: {recordInfo.channels.GetChannelString()}");               
        }

        async Task MainAsync(RecordInfo recordInfo,IConfiguration configuration)
        {
            //Write to our very own log as there might be other captures going too
            string logPath=Path.Combine(configuration["logPath"],recordInfo.fileName+"Log.txt");
            FileStream fileHandle = new FileStream (logPath, FileMode.OpenOrCreate, FileAccess.Write);
            StreamWriter logWriter = new StreamWriter (fileHandle);
            logWriter.AutoFlush = true;

            //Dump
            DumpRecordInfo(logWriter,recordInfo,"MainAsync");

            //Wait here until we're ready to start recording
            if(recordInfo.strStartDT != null)
            {
                DateTime recStart = recordInfo.GetStartDT();
                TimeSpan timeToWait = recStart - DateTime.Now;
                logWriter.WriteLine($"{DateTime.Now}: Starting recording at {recStart} - Waiting for {timeToWait.Days} Days, {timeToWait.Hours} Hours, and {timeToWait.Minutes} minutes.");
                if(timeToWait.Seconds>0)
                    Thread.Sleep(timeToWait);
            }       

            //Authenticate
            string hashValue = await Authenticate(configuration);

            //Capture stream
            int numFiles=CaptureStream(logWriter,hashValue,recordInfo,configuration);

            //Fixup output
            FixUp(logWriter,numFiles,recordInfo,configuration);

            //Cleanup
            logWriter.WriteLine($"{DateTime.Now}: Done Capturing - Cleaning up");
            logWriter.Dispose();
            fileHandle.Dispose();
        }

        private async Task<string> Authenticate(IConfiguration configuration)
        {
            string hashValue=null;

            using (var client = new HttpClient())
            {
                //http://smoothstreams.tv/schedule/admin/dash_new/hash_api.php?username=foo&password=bar&site=view247

                //Build URL
                string strURL=configuration["authURL"];
                strURL=strURL.Replace("[USERNAME]",configuration["user"]);
                strURL=strURL.Replace("[PASSWORD]",configuration["pass"]);

                var response = await client.GetAsync(strURL);
                response.EnsureSuccessStatusCode(); // Throw in not success

                string stringResponse = await response.Content.ReadAsStringAsync();
                
                //Console.WriteLine($"Response: {stringResponse}");

                //Grab hash
                JsonTextReader reader = new JsonTextReader(new StringReader(stringResponse));
                while (reader.Read())
                {
                    if (reader.Value != null)
                    {
                        //Console.WriteLine("Token: {0}, Value: {1}", reader.TokenType, reader.Value);
                        if(reader.TokenType.ToString() == "PropertyName" && reader.Value.ToString() == "hash")
                        {
                            reader.Read();
                            hashValue=reader.Value.ToString();
                            break;
                        }
                    }
                }
            }

            return hashValue;
        }
     
        private int CaptureStream(TextWriter logWriter,string hashValue,RecordInfo recordInfo,IConfiguration configuration)
        {
            int currentFileNum = 0;
            int channelIdx = 0;
            int currentChannelFailureCount = 0;

            //Marking time we started and when we should be done
            DateTime captureStarted = DateTime.Now;
            DateTime captureTargetEnd = captureStarted.AddMinutes(recordInfo.GetDuration());
            DateTime lastStartedTime = captureStarted;


            //Getting channel list
            ChannelInfo[] channelInfoArray=recordInfo.channels.GetSortedChannels();
            ChannelInfo currentChannel=channelInfoArray[channelIdx];

            //Update capture history
            recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).recordingsAttempted+=1;
            recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).lastAttempt=DateTime.Now;

            //Build ffmpeg capture command line with first channel and get things rolling
            string cmdLineArgs=BuildCaptureCmdLineArgs(currentChannel.number,hashValue,recordInfo.fileName+currentFileNum,configuration);
            logWriter.WriteLine($"{DateTime.Now}: Starting {captureStarted} and expect to be done {captureTargetEnd}.  Cmd: {configuration["ffmpegPath"]} {cmdLineArgs}");
            Process p = ExecProcess(logWriter,configuration["ffmpegPath"],cmdLineArgs,recordInfo.GetDuration());  //setting async flag
            logWriter.WriteLine($"{DateTime.Now}: After execution.  Exit Code: {p.ExitCode}");

            //
            //retry loop if we're not done yet
            //
            int numRetries=Convert.ToInt32(configuration["numberOfRetries"]);
            for(int retryNum=0;DateTime.Now<captureTargetEnd && retryNum<numRetries;retryNum++)
            {           
                logWriter.WriteLine($"{DateTime.Now}: Capture Failed for channel {currentChannel.number}. Last failure {lastStartedTime}  Retry {retryNum+1} of {configuration["numberOfRetries"]}");

                //Update channel history
                recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).errors+=1;

                //increment failure count and file number
                currentChannelFailureCount++;
                currentFileNum++;

                //Got to next channel if channel has been alive for less than 15 minutes
                TimeSpan fifteenMin=new TimeSpan(0,15,0);
                if((DateTime.Now-lastStartedTime) < fifteenMin && !recordInfo.bestChannelSetFlag)
                {
                    //Set quality ratio for current channel
                    int minutes = (DateTime.Now-lastStartedTime).Minutes;
                    double qualityRatio=minutes/currentChannelFailureCount;
                    currentChannel.ratio=qualityRatio;
                    logWriter.WriteLine($"{DateTime.Now}: Setting quality ratio {qualityRatio} for channel {currentChannel.number}");

                    //Determine correct next channel based on number and quality
                    if(!recordInfo.bestChannelSetFlag)
                    {
                        channelIdx=GetNextChannel(logWriter,recordInfo,channelInfoArray,channelIdx);
                        currentChannel=channelInfoArray[channelIdx];
                    }

                    currentChannelFailureCount=0;
                }

                //Set new started time and calc new timer     
                TimeSpan timeJustRecorded=DateTime.Now-lastStartedTime;
                lastStartedTime = DateTime.Now;
                TimeSpan timeLeft=captureTargetEnd-DateTime.Now;

                //Update capture history
                recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).hoursRecorded+=timeJustRecorded.TotalHours;
                recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).recordingsAttempted+=1;
                recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).lastAttempt=DateTime.Now;
                recordInfo.channelHistory.Save();

                //Now get things setup and going again
                cmdLineArgs=BuildCaptureCmdLineArgs(currentChannel.number,hashValue,recordInfo.fileName+currentFileNum,configuration);
                logWriter.WriteLine($"{DateTime.Now}: Starting Capture (again): {configuration["ffmpegPath"]} {cmdLineArgs}");
                p = ExecProcess(logWriter,configuration["ffmpegPath"],cmdLineArgs,(int)timeLeft.TotalMinutes+1);
            }
            logWriter.WriteLine($"{DateTime.Now}: Finished Capturing Stream.");

            //Update capture history
            TimeSpan finalTimeJustRecorded=DateTime.Now-lastStartedTime;
            recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).hoursRecorded+=finalTimeJustRecorded.TotalHours;
            recordInfo.channelHistory.GetChannelHistoryInfo(currentChannel.number).lastSuccess=DateTime.Now;
            recordInfo.channelHistory.Save();

            return currentFileNum;
        }

        private int GetNextChannel(TextWriter logWriter,RecordInfo recordInfo,ChannelInfo[] channelInfoArray,int channelIdx)
        {   
            //Oportunistically get next channel
            channelIdx++;

            if(channelIdx < channelInfoArray.Length)  
            {
                //do we still have more channels?  If so, grab the next one
                logWriter.WriteLine($"{DateTime.Now}: Switching to channel {channelInfoArray[channelIdx].number}");
            }
            else
            {
                //grab best channel by grabbing the best ratio  
                double ratio=0;
                for(int b=0;b<channelInfoArray.Length;b++)
                {
                    if(channelInfoArray[b].ratio>=ratio)
                    {
                        ratio=channelInfoArray[b].ratio;
                        channelIdx=b;         
                        recordInfo.bestChannelSetFlag=true;
                    }
                }
                logWriter.WriteLine($"{DateTime.Now}: Now setting channel to {channelInfoArray[channelIdx].number} with quality ratio of {channelInfoArray[channelIdx].ratio} for the rest of the capture session");
            }

            return channelIdx;
        }

        private string BuildCaptureCmdLineArgs(string channel,string hashValue,string fileName,IConfiguration configuration)
        {
            //C:\Users\mark\Desktop\ffmpeg\bin\ffmpeg -i "http://dnaw1.smoothstreams.tv:9100/view247/ch01q1.stream/playlist.m3u8?wmsAuthSign=c2VydmVyX3RpbWU9MTIvMS8yMDE2IDM6NDA6MTcgUE0maGFzaF92YWx1ZT1xVGxaZmlzMkNYd0hFTEJlaTlzVVJ3PT0mdmFsaWRtaW51dGVzPTcyMCZpZD00MzM=" -c copy t.ts
            
            string cmdLineArgs = configuration["captureCmdLine"];
            string outputPath = Path.Combine(configuration["outputPath"],fileName+".ts");

            cmdLineArgs=cmdLineArgs.Replace("[FULLOUTPUTPATH]",outputPath);
            cmdLineArgs=cmdLineArgs.Replace("[CHANNEL]",channel);
            cmdLineArgs=cmdLineArgs.Replace("[AUTHTOKEN]",hashValue);
            
            //Console.WriteLine($"Cmd Line Args:  {cmdLineArgs}");

            return cmdLineArgs;
        }

        private Process ExecProcess(TextWriter logWriter,string exe,string cmdLineArgs)
        {
            return ExecProcess(logWriter,exe,cmdLineArgs,0);
        }

        private Process ExecProcess(TextWriter logWriter,string exe,string cmdLineArgs,int timeout)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = cmdLineArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Timer captureTimer=null;
            Process process = Process.Start(processInfo);

            //Let's build a timer to kill the process when done
            if(timeout>0)
            {
                TimeSpan delayTime = new TimeSpan(0, timeout, 0);
                TimeSpan intervalTime = new TimeSpan(0, 0, 0, 0, -1); //turn off interval timer
                logWriter.WriteLine($"{DateTime.Now}: Settting Timer for {timeout} minutes in the future to kill process.");
                captureTimer = new Timer(OnCaptureTimer, process, delayTime, intervalTime);
            }

            //Now, let's wait for the thing to exit
            logWriter.WriteLine(process.StandardError.ReadToEnd());
            logWriter.WriteLine(process.StandardOutput.ReadToEnd());
            process.WaitForExit();

            //Clean up timer
            if(timeout>0 && captureTimer != null)
                captureTimer.Dispose();

            return process;
        }

        //Handler for ffmpeg timer to kill the process
        static void OnCaptureTimer(object obj)
        {    
            Process p = obj as Process;
            if(p!=null && !p.HasExited)
            {
                p.Kill();
                p.WaitForExit();
            }                
        }

        private void FixUp(TextWriter logWriter,int numFiles,RecordInfo recordInfo,IConfiguration configuration)
        {
            string cmdLineArgs;
            string outputFile;
            string videoFileName=recordInfo.fileName+"0.ts";
            string ffmpegPath = configuration["ffmpegPath"];
            string outputPath = configuration["outputPath"];

            //metadata
            string metadata;
            if(recordInfo.description!=null)
                metadata=recordInfo.description;
            else
                metadata="File Name: "+recordInfo.fileName;


            //Concat if more than one file
            logWriter.WriteLine($"{DateTime.Now}: Num Files: {(numFiles+1)}");
            if(numFiles > 0)
            {
                //make fileist
                string fileList = Path.Combine(outputPath,recordInfo.fileName+"0.ts");
                for(int i=1;i<=numFiles;i++)
                    fileList=fileList+"|"+Path.Combine(outputPath,recordInfo.fileName+i+".ts");

                //Create output file path
                outputFile=Path.Combine(outputPath,recordInfo.fileName+".ts");

                //"concatCmdLine": "[FULLFFMPEGPATH] -i \"concat:[FILELIST]\" -c copy [FULLOUTPUTPATH]",
                cmdLineArgs = configuration["concatCmdLine"];
                cmdLineArgs=cmdLineArgs.Replace("[FILELIST]",fileList);
                cmdLineArgs=cmdLineArgs.Replace("[FULLOUTPUTPATH]",outputFile);

                //Run command
                logWriter.WriteLine($"{DateTime.Now}: Starting Concat: {configuration["ffmpegPath"]} {cmdLineArgs}");
                ExecProcess(logWriter,configuration["ffmpegPath"],cmdLineArgs);

                videoFileName=recordInfo.fileName+".ts";
            }

            //Mux file to mp4 from ts (transport stream)
            string inputFile=Path.Combine(outputPath,videoFileName);
            outputFile=Path.Combine(outputPath,recordInfo.fileName+".mp4");

            // "muxCmdLine": "[FULLFFMPEGPATH] -i [VIDEOFILE] -acodec copy -vcodec copy [FULLOUTPUTPATH]"
            cmdLineArgs = configuration["muxCmdLine"];
            cmdLineArgs=cmdLineArgs.Replace("[VIDEOFILE]",inputFile);
            cmdLineArgs=cmdLineArgs.Replace("[FULLOUTPUTPATH]",outputFile);
            cmdLineArgs=cmdLineArgs.Replace("[DESCRIPTION]",recordInfo.description);            

            //Run command
            logWriter.WriteLine($"{DateTime.Now}: Starting Mux: {configuration["ffmpegPath"]} {cmdLineArgs}");
            ExecProcess(logWriter,configuration["ffmpegPath"],cmdLineArgs);

            //If final file exist, delete old .ts file/s
            if(File.Exists(outputFile))
            {
                inputFile=Path.Combine(outputPath,videoFileName);
                logWriter.WriteLine($"{DateTime.Now}: Removing ts file: {inputFile}");
                File.Delete(inputFile);

                for(int i=0;i<=numFiles;i++)
                {
                    inputFile=Path.Combine(outputPath,recordInfo.fileName+i+".ts");
                    logWriter.WriteLine($"{DateTime.Now}: Removing ts file: {inputFile}");
                    File.Delete(inputFile);
                }
            }
        }
    }
}
