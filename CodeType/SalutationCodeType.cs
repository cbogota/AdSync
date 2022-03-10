using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utility
{
    public class SalutationCodeType : ICodeType
    {
        private const string CodeTypeKey = "Salutation";
        public string CodeType => CodeTypeKey;
    }
}
