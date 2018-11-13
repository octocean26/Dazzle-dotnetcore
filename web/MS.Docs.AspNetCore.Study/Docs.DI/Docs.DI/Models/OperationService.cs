﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Docs.DI.Models
{
    public class OperationService
    {
        public OperationService(
            IOperationTransient transientOperation,
            IOperationScoped scopedOperation,
            IOperationSingleton singletonOperation, 
            IOperationSingletonInstance instanceOperation)
        {
            this.TransientOperation = transientOperation;
            this.ScopedOperation = ScopedOperation;
            this.SingletonOperation = singletonOperation;
            this.SingletonInstanceOperation = instanceOperation;
        }

        public IOperationTransient TransientOperation { get; }
        public IOperationScoped ScopedOperation { get; }
        public IOperationSingleton SingletonOperation { get; }
        public IOperationSingletonInstance SingletonInstanceOperation { get; }
    }
}
