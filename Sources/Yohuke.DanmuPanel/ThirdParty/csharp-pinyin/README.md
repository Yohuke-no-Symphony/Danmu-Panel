# csharp-pinyin

## Intro

csharp-pinyin is a lightweight Chinese/Cantonese to Pinyin library.

Chinese dialects can be used to create their own dictionaries using makedict.

Initial version algorithm reference [zh_CN](https://github.com/ZiQiangWang/zh_CN), and undergo significant optimization.

[pinyin-makedict](https://github.com/wolfgitpr/pinyin-makedict) is the tool for creating Chinese/Cantonese dictionaries.

## Feature

+ Interface reference [pypinyin](https://github.com/mozillazg/python-pinyin)

+ Only Unicode within the range of  [ 0x4E00 - 0x9FFF ]  is supported.

+ Segmentation for heteronym words.

+ Support Traditional and Simplified Chinese.

+ Speed is very fast, about 500,000 words/s.

+ Achieved an accuracy rate of 99.9% on a 200000 word Lyrics-Pinyin test set Without-Tone.

+ The With-Tone test on CPP_Dataset(about 79k sentences) achieved an accuracy of 90.3%, while the accuracy of pypinyin
  was approximately 87%.

## Usage

```csharp
using Pinyin;

Pinyin.Pinyin pinyinInstance = Pinyin.Pinyin.Instance; // or Pinyin.Jyutping.Instance;
string hans = "明月@1几32时有##一";
PinyinResVector pinyinRes = pinyinInstance.HanziToPinyin(key, ManTone.Style.NORMAL, Error.Default, false, false, false);

List<string> pinyin = pinyinInstance.GetDefaultPinyin("了", ManTone.Style.TONE3, false, false);
```

## Doc

```csharp
//  include/ChineseG2p.cs
public struct PinyinRes
{
    public string hanzi;               //  utf-16 string
    public string pinyin;              //  utf-16 string
    public List<string> candidates;    //  Candidate pinyin of Polyphonic Characters.
    public bool error;                 //  Whether the conversion failed.
};

public class PinyinResList : List<PinyinRes>
{
public:
    //  Convert to utf-16 string list.
    public List<string> ToStrList();
    //  Convert to utf-16 string with delimiter(default: " ").
    public string ToStr(string delimiter = " ");
};

//  ChineseG2p.cs
  enum class Error {
      // Keep original characters
      Default = 0,
      // Ignore this character (do not export)
      Ignore = 1
  };

/*
    @param hans : raw utf-16 string.
    @param ManTone.Style : Preserve the pinyin tone.
    @param errorType : Ignore words that have failed conversion. Default: Keep original.
    @param candidates : Return all possible pinyin candidates. Default: true.
    @param v_to_u : Convert v to ü. Default: false.
    @param neutral_tone_with_five : Use 5 as neutral tone. Default: false.
    @return PinyinResList.
*/
public PinyinResList HanziToPinyin(string hans,
                                        ManTone.Style style = ManTone.Style.TONE,
                                        Error error = Error.Default, bool candidates = true,
                                        bool vToU = false, bool neutralToneWithFive = false);

/*
    @param hans : raw utf-16 List<string>, each element of the vector is a character.
    ...
    @return PinyinResList.
*/
public PinyinResList HanziToPinyin(List<string> hans,
                                        ManTone.Style style = ManTone.Style.TONE,
                                        Error error = Error.Default, bool candidates = true,
                                        bool vToU = false, bool neutralToneWithFive = false);

//  Convert to Simplified Chinese.  utf-8 std::string
string TradToSim(string text);

//  Determine if it is a polyphonic character.
bool IsPolyphonic(string text);

//  Get a pronunciation list.
public List<string> GetDefaultPinyin(string hanzi,
                                     ManTone.Style style = ManTone.Style.TONE,
                                     bool vToU = false, bool neutralToneWithFive = false);
```

## Open-source softwares used

+ [zh_CN](https://github.com/ZiQiangWang/zh_CN)
  The core algorithm source has been further tailored to the dictionary in this project.

+ [opencpop](http://wenet.org.cn/opencpop/)
  The test data source.

+ [M4Singer](https://github.com/M4Singer/M4Singer)
  The test data source.

+ [cc-edict](https://cc-cedict.org/wiki/)
  The dictionary source.

+ [pinyin](https://github.com/kfcd/pinyin)
  The fan-jian dictionary source.

+ [cpp_dataset](https://github.com/kakaobrain/g2pm/tree/master/data)
  The cpp_dataset source.

## Related Projects

+ [pinyin-makedict](https://github.com/wolfgitpr/pinyin-makedict)
  A tool for creating Chinese/Cantonese dictionaries.

+ [cpp-pinyin](https://github.com/wolfgitpr/cpp-pinyin)
  A C++ implementation of Chinese/Cantonese to Pinyin library.

+ [python-pinyin](https://github.com/mozillazg/python-pinyin)
  A Python implementation of Chinese/Cantonese to Pinyin library.
