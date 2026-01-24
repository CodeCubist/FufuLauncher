using System.Collections.ObjectModel;

namespace FufuLauncher.Models
{
    public class WeeklyPlayTimeStats
    {
        public double TotalHours
        {
            get; set;
        }
        public double AverageHours
        {
            get; set;
        }
        public string TotalHoursFormatted => $"{TotalHours:F1}h";
        public string AverageHoursFormatted => $"{AverageHours:F1}h";
        public ObservableCollection<GamePlayTimeRecord> DailyRecords { get; set; } = new();
    }
    public class GamePlayTimeRecord
    {
        public DateTime Date
        {
            get; set;
        }
        public long PlayTimeSeconds
        {
            get; set;
        }

        public TimeSpan PlayTime => TimeSpan.FromSeconds(PlayTimeSeconds);
        public string DisplayDate => Date.ToString("MM-dd");
        public string DayOfWeek => GetDayOfWeekString(Date.DayOfWeek);
        public string DisplayTime => PlayTime.TotalHours >= 1 ?
            $"{(int)PlayTime.TotalHours}h {PlayTime.Minutes}m" :
            $"{PlayTime.Minutes}m";

        private static string GetDayOfWeekString(System.DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                System.DayOfWeek.Sunday => "周日",
                System.DayOfWeek.Monday => "周一",
                System.DayOfWeek.Tuesday => "周二",
                System.DayOfWeek.Wednesday => "周三",
                System.DayOfWeek.Thursday => "周四",
                System.DayOfWeek.Friday => "周五",
                System.DayOfWeek.Saturday => "周六",
                _ => ""
            };
        }
    }
}