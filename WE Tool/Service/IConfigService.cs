using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Models;

namespace WE_Tool.Service
{
    public interface IConfigService
    {
        Task<AppSettings> LoadAsync();
        Task SaveAsync(AppSettings settings);
    }
}
