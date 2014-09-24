﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wyam.Core
{
    public class PipelineCollection : IEnumerable<Pipeline>
    {
        private readonly Engine _engine;
        private readonly List<Pipeline> _pipelines = new List<Pipeline>();

        public PipelineCollection(Engine engine)
        {
            _engine = engine;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerator<Pipeline> GetEnumerator()
        {
            return _pipelines.GetEnumerator();
        }

        public void Add(Pipeline pipeline)
        {
            _pipelines.Add(pipeline);
        }

        public void Add(params IModule[] modules)
        {
            _pipelines.Add(new Pipeline(_engine, modules));
        }
    }
}
