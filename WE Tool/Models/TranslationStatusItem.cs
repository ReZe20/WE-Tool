namespace WE_Tool.Models
{
    /// <summary>单个语言的翻译完成度(构建时由 TranslationStatus.g.cs 生成数据)</summary>
    public class TranslationStatusItem
    {
        public string Locale { get; set; } = "";
        public string Name { get; set; } = "";
        public int Percent { get; set; }
        public string PercentText => $"{Percent}%";
    }
}
