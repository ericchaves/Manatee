{
    up:{
        create_table:{
            name:'SecondTable',
            columns:[
                {name:'name', type:'string'},
                {name:'description', type:'text'},
                {name:'firstTableID',type:'int'},
			    {name:'amount',type:'money_4'}
             ],
            timestamps:true
         }
    }
}