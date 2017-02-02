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
    public class Keywords
    {
        Dictionary<string, KeywordInfo> keywordDict;

        public Keywords()
        {
            keywordDict = JsonConvert.DeserializeObject<Dictionary<string, KeywordInfo>>(File.ReadAllText("keywords.json"));
        }

        public KeywordInfo[] GetKeywordArray()
        {
            return keywordDict.Values.ToArray();
        }

        //Given a show name, see if there's a match in any of the keywords
        public KeywordInfo FindMatch(string showName)
        {
            KeywordInfo keywordInfo = null;

            //Go through keywords seeing if there's a match
            foreach (KeyValuePair<string, KeywordInfo> kvp in keywordDict)
            {
                string strKeywords = kvp.Value.keywords;
                string[] kArray = strKeywords.Split(',');

                for (int i = 0; i < kArray.Length; i++)
                {
                    if (showName.ToLower().Contains(kArray[i].ToLower()))
                    {
                        string[] excludeArray = kvp.Value.exclude.Split(',');

                        //Make sure no keywords to exclude are found
                        bool excludedFlag=false;
                        for(int e=0;e<excludeArray.Length;e++)
                        {
                            if (showName.ToLower().Contains(excludeArray[e].ToLower()))
                                excludedFlag=true;
                        }

                        if(!excludedFlag)
                        {
                            keywordInfo = kvp.Value;
                            break;
                        }
                    }
                }
            }

            return keywordInfo;
        }
    }
}
 